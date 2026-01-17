using Alberto.Dcb.Subscriptions.Pipeline;
using Alberto.Dcb.Telemetry;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Polling-based event consumer that routes events from the store to processors.
/// Supports optional leader election for single-instance processing.
/// </summary>
public sealed class PollingConsumer : IEventConsumer
{
    private readonly IEventStoreBackend _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IDeadLetterStore? _deadLetterStore;
    private readonly IConsumeFilterPipeline? _pipeline;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly int _rebuildBatchSize;
    private readonly long _rebuildThreshold;
    private readonly IProcessorLock? _processorLock;
    private readonly ErrorPolicy _errorPolicy;
    private readonly string _moduleKey;

    private readonly List<IEventProcessor> _processors = [];
    private readonly Dictionary<string, Task> _rebuildTasks = [];
    private readonly object _rebuildTasksLock = new();
    private IAsyncDisposable? _lockLease;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public PollingConsumer(
        IEventStoreBackend eventStore,
        ICheckpointStore checkpointStore,
        string consumerId,
        string moduleKey = "",
        TimeSpan? pollingInterval = null,
        int batchSize = 100,
        IProcessorLock? processorLock = null,
        IDeadLetterStore? deadLetterStore = null,
        IConsumeFilterPipeline? pipeline = null,
        ErrorPolicy? errorPolicy = null,
        int rebuildBatchSize = 1000,
        long rebuildThreshold = 1000)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        ConsumerId = consumerId ?? throw new ArgumentNullException(nameof(consumerId));
        _moduleKey = moduleKey;
        _pollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(100);
        _batchSize = batchSize;
        _rebuildBatchSize = rebuildBatchSize;
        _rebuildThreshold = rebuildThreshold;
        _processorLock = processorLock;
        _deadLetterStore = deadLetterStore;
        _pipeline = pipeline;
        _errorPolicy = errorPolicy ?? ErrorPolicy.Default;
    }

    /// <inheritdoc />
    public string ConsumerId { get; }

    /// <inheritdoc />
    public void RegisterProcessor(IEventProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processors.Add(processor);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_pollingTask is not null)
            throw new InvalidOperationException("Consumer is already running.");

        // Try to acquire leadership if lock is configured
        if (_processorLock is not null)
        {
            _lockLease = await _processorLock.TryAcquireAsync(ConsumerId, ct);
            if (_lockLease is null)
            {
                // Not leader - exit without starting
                return;
            }
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollingTask = PollLoopAsync(_cts.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        // Wait for main polling task
        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        // Wait for all rebuild tasks to complete
        Task[] rebuildTasksCopy;
        lock (_rebuildTasksLock)
        {
            rebuildTasksCopy = _rebuildTasks.Values.ToArray();
        }

        if (rebuildTasksCopy.Length > 0)
        {
            try
            {
                await Task.WhenAll(rebuildTasksCopy);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        if (_lockLease is not null)
        {
            await _lockLease.DisposeAsync();
            _lockLease = null;
        }

        _cts?.Dispose();
        _cts = null;
        _pollingTask = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        foreach (var processor in _processors)
        {
            if (processor is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Get global position to classify processors
                var globalPosition = await _eventStore.GetLastPositionGlobal(ct);

                // Classify processors into active (caught-up) vs rebuilding
                var (activeCheckpoints, newRebuilders) = await ClassifyProcessorsAsync(globalPosition, ct);

                // Spawn independent tasks for newly identified rebuilding processors
                foreach (var processor in newRebuilders)
                {
                    lock (_rebuildTasksLock)
                    {
                        if (!_rebuildTasks.ContainsKey(processor.ProcessorId))
                        {
                            _rebuildTasks[processor.ProcessorId] = RebuildProcessorAsync(processor, ct);
                        }
                    }
                }

                // Main loop only processes active (caught-up) processors
                if (activeCheckpoints.Count == 0)
                {
                    await Task.Delay(_pollingInterval, ct);
                    continue;
                }

                var minPosition = activeCheckpoints.Values.Min();

                // Fetch events from global stream
                var events = await _eventStore.StreamGlobal(minPosition, _batchSize, ct);

                var processedAny = false;

                if (events.Count > 0)
                {
                    // Route to each active processor, filtering by their individual checkpoint
                    foreach (var processor in _processors)
                    {
                        if (!processor.IsActive || processor.IsRebuilding)
                            continue;

                        var processorCheckpoint = activeCheckpoints.GetValueOrDefault(processor.ProcessorId, 0);

                        var relevant = events
                            .Where(e => e.GlobalPosition > processorCheckpoint)
                            .Where(e => processor.HandledEventTypes.Contains(e.EventType.Id))
                            .ToList();

                        if (relevant.Count > 0)
                        {
                            await ProcessEventsForProcessorAsync(processor, relevant, processorCheckpoint, ct);
                            processedAny = true;
                        }
                    }
                }

                // Wait before polling again if no events were processed
                if (!processedAny)
                {
                    await Task.Delay(_pollingInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
        }
    }

    private async Task ProcessEventsForProcessorAsync(
        IEventProcessor processor,
        IReadOnlyList<IEventEnvelope> events,
        long currentCheckpoint,
        CancellationToken ct)
    {
        var context = new ConsumeContext(processor.ProcessorId, _moduleKey, currentCheckpoint);

        // The processing action that handles each event with error handling
        async Task ProcessingAction()
        {
            foreach (var evt in events)
            {
                if (!processor.IsActive)
                    break;

                await ProcessSingleEventAsync(processor, evt, ct);
            }

            // Flush batched changes after processing all events
            if (processor is IFlushable flushable)
            {
                await flushable.FlushAsync(ct);
            }
        }

        // Execute through pipeline if available
        if (_pipeline is not null)
        {
            await _pipeline.ExecuteAsync(events, context, ProcessingAction, ct);
        }
        else
        {
            await ProcessingAction();
        }
    }

    private async Task ProcessSingleEventAsync(
        IEventProcessor processor,
        IEventEnvelope evt,
        CancellationToken ct)
    {
        var attempts = 0;
        var concurrencyRetries = 0;
        const int maxConcurrencyRetries = 5;

        while (true)
        {
            try
            {
                attempts++;
                await processor.ProcessEventAsync(evt, ct);

                // Success - save checkpoint and move on
                await _checkpointStore.SaveAsync(processor.ProcessorId, evt.GlobalPosition, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate cancellation
            }
            catch (ConcurrencyConflictException)
            {
                // Optimistic concurrency conflict - retry immediately without counting against retry limit
                concurrencyRetries++;
                if (concurrencyRetries >= maxConcurrencyRetries)
                {
                    // Too many concurrency conflicts, fall through to normal error handling
                    throw;
                }

                // Brief delay to allow other transaction to complete
                await Task.Delay(TimeSpan.FromMilliseconds(10 * concurrencyRetries), ct);
                attempts--; // Don't count this against retry limit
                continue;
            }
            catch (Exception ex)
            {
                var decision = processor.HandleError(evt, ex, attempts, _errorPolicy);

                switch (decision)
                {
                    case ErrorHandlingDecision.Retry:
                        // Record retry metric
                        AlbertoMetrics.Retries.Add(1,
                            new KeyValuePair<string, object?>("processor", processor.ProcessorId),
                            new KeyValuePair<string, object?>("module", _moduleKey));

                        // Use exponential backoff for retry delay
                        var delay = _errorPolicy.CalculateDelay(attempts);
                        await Task.Delay(delay, ct);
                        continue;

                    case ErrorHandlingDecision.DeadLetter:
                        await DeadLetterEventAsync(processor.ProcessorId, evt, ex, attempts, ct);
                        await _checkpointStore.SaveAsync(processor.ProcessorId, evt.GlobalPosition, ct);
                        return;

                    case ErrorHandlingDecision.Skip:
                        await _checkpointStore.SaveAsync(processor.ProcessorId, evt.GlobalPosition, ct);
                        return;

                    case ErrorHandlingDecision.Stop:
                        processor.IsActive = false;
                        return;
                }
            }
        }
    }

    private async Task DeadLetterEventAsync(
        string processorId,
        IEventEnvelope evt,
        Exception ex,
        int attempts,
        CancellationToken ct)
    {
        // Record dead letter counter
        AlbertoMetrics.DeadLetters.Add(1,
            new KeyValuePair<string, object?>("processor", processorId),
            new KeyValuePair<string, object?>("module", _moduleKey));

        if (_deadLetterStore is null)
            return;

        var entry = new DeadLetterEntry(
            Id: Guid.NewGuid(),
            ProcessorId: processorId,
            EventId: evt.Id,
            EventType: evt.EventType.Id,
            EventData: evt.EventData,
            ErrorMessage: ex.Message,
            StackTrace: ex.StackTrace,
            AttemptCount: attempts,
            FailedAt: DateTimeOffset.UtcNow);

        await _deadLetterStore.StoreAsync(entry, ct);
    }

    /// <summary>
    /// Classifies processors into active (caught-up) and rebuilding (far behind) groups.
    /// Processors lagging beyond the rebuild threshold are marked for independent rebuilding.
    /// </summary>
    private async Task<(Dictionary<string, long> ActiveCheckpoints, List<IEventProcessor> NewRebuilders)>
        ClassifyProcessorsAsync(long globalPosition, CancellationToken ct)
    {
        var activeCheckpoints = new Dictionary<string, long>();
        var newRebuilders = new List<IEventProcessor>();

        foreach (var processor in _processors)
        {
            if (!processor.IsActive)
                continue;

            // Skip processors already rebuilding (they have their own task)
            if (processor.IsRebuilding)
                continue;

            var position = await _checkpointStore.GetAsync(processor.ProcessorId, ct) ?? 0;
            var lag = globalPosition - position;

            // Record processor lag metric
            AlbertoMetrics.RecordProcessorLag(processor.ProcessorId, _moduleKey, lag);

            if (lag > _rebuildThreshold)
            {
                // Mark as rebuilding - will spawn independent task
                processor.IsRebuilding = true;
                newRebuilders.Add(processor);
            }
            else
            {
                activeCheckpoints[processor.ProcessorId] = position;
            }
        }

        return (activeCheckpoints, newRebuilders);
    }

    /// <summary>
    /// Independent rebuild task for a processor that is far behind.
    /// Runs until the processor catches up within the threshold, then rejoins the main loop.
    /// </summary>
    private async Task RebuildProcessorAsync(IEventProcessor processor, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && processor.IsActive && processor.IsRebuilding)
            {
                var currentPos = await _checkpointStore.GetAsync(processor.ProcessorId, ct) ?? 0;
                var globalPos = await _eventStore.GetLastPositionGlobal(ct);

                // Check if caught up (within threshold)
                if (globalPos - currentPos <= _rebuildThreshold)
                {
                    processor.IsRebuilding = false;
                    return;
                }

                // Fetch larger batch for faster catch-up
                var events = await _eventStore.StreamGlobal(currentPos, _rebuildBatchSize, ct);

                if (events.Count == 0)
                {
                    await Task.Delay(_pollingInterval, ct);
                    continue;
                }

                // Process events for this processor only
                var relevant = events
                    .Where(e => e.GlobalPosition > currentPos)
                    .Where(e => processor.HandledEventTypes.Contains(e.EventType.Id))
                    .ToList();

                foreach (var evt in relevant)
                {
                    if (!processor.IsActive || !processor.IsRebuilding)
                        break;

                    await ProcessSingleEventAsync(processor, evt, ct);
                }

                // Flush after processing rebuild batch
                if (processor is IFlushable flushable)
                {
                    await flushable.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        finally
        {
            processor.IsRebuilding = false;
            lock (_rebuildTasksLock)
            {
                _rebuildTasks.Remove(processor.ProcessorId);
            }
        }
    }

    /// <summary>
    /// Triggers a manual rebuild for a specific processor.
    /// Resets the checkpoint to 0 and starts rebuilding from the beginning.
    /// </summary>
    /// <param name="processorId">The processor ID to rebuild.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if rebuild was started, false if processor not found or already rebuilding.</returns>
    public async Task<bool> TriggerRebuildAsync(string processorId, CancellationToken ct = default)
    {
        var processor = _processors.FirstOrDefault(p => p.ProcessorId == processorId);
        if (processor is null)
            return false;

        if (processor.IsRebuilding)
            return false;

        // Reset checkpoint to 0
        await _checkpointStore.SaveAsync(processorId, 0, ct);

        // Mark as rebuilding and spawn task
        processor.IsRebuilding = true;

        lock (_rebuildTasksLock)
        {
            if (!_rebuildTasks.ContainsKey(processorId) && _cts is not null)
            {
                _rebuildTasks[processorId] = RebuildProcessorAsync(processor, _cts.Token);
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the current rebuild status for all processors.
    /// </summary>
    public IReadOnlyList<(string ProcessorId, bool IsRebuilding)> GetRebuildStatuses()
    {
        return _processors
            .Select(p => (p.ProcessorId, p.IsRebuilding))
            .ToList();
    }
}
