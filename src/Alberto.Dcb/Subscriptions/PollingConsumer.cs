using Alberto.Dcb.Subscriptions.Pipeline;

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
    private readonly IProcessorLock? _processorLock;
    private readonly ErrorPolicy _errorPolicy;
    private readonly string _moduleKey;

    private readonly List<IEventProcessor> _processors = [];
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
        ErrorPolicy? errorPolicy = null)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        ConsumerId = consumerId ?? throw new ArgumentNullException(nameof(consumerId));
        _moduleKey = moduleKey;
        _pollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(100);
        _batchSize = batchSize;
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
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Load all processor checkpoints
                var checkpoints = await GetProcessorCheckpointsAsync(ct);
                var minPosition = checkpoints.Count > 0 ? checkpoints.Values.Min() : 0;

                // Fetch events from global stream
                var events = await _eventStore.StreamGlobal(minPosition, _batchSize, ct);

                var processedAny = false;

                if (events.Count > 0)
                {
                    // Route to each processor, filtering by their individual checkpoint
                    foreach (var processor in _processors)
                    {
                        if (!processor.IsActive)
                            continue;

                        var processorCheckpoint = checkpoints.GetValueOrDefault(processor.ProcessorId, 0);

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
            catch (Exception ex)
            {
                var decision = processor.HandleError(evt, ex, attempts, _errorPolicy);

                switch (decision)
                {
                    case ErrorHandlingDecision.Retry:
                        await Task.Delay(_errorPolicy.RetryDelay, ct);
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

    private async Task<Dictionary<string, long>> GetProcessorCheckpointsAsync(CancellationToken ct)
    {
        var checkpoints = new Dictionary<string, long>();

        foreach (var processor in _processors)
        {
            if (!processor.IsActive)
                continue;

            var position = await _checkpointStore.GetAsync(processor.ProcessorId, ct) ?? 0;
            checkpoints[processor.ProcessorId] = position;
        }

        return checkpoints;
    }
}
