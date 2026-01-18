using System.Collections.Concurrent;
using Alberto.Dcb.Subscriptions.Pipeline;
using Alberto.Dcb.Telemetry;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Polling-based event consumer that routes events from the store to processors.
/// Supports optional leader election for single-instance processing.
/// Supports tenant-distributed mode where multiple instances each claim different tenants.
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
    private readonly ITenantProcessorLock? _tenantProcessorLock;
    private readonly ConsumerDistributionMode _distributionMode;
    private readonly TimeSpan _tenantLockRetryInterval;
    private readonly int _maxParallelProjections;
    private readonly ErrorPolicy _errorPolicy;
    private readonly string _moduleKey;
    private readonly ILogger<PollingConsumer>? _logger;

    private readonly List<IEventProcessor> _processors = [];
    private readonly Dictionary<string, Task> _rebuildTasks = [];
    private readonly object _rebuildTasksLock = new();
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _tenantLeases = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tenantLockCooldowns = new();
    private readonly HashSet<string> _ownedTenants = new();
    private readonly object _tenantLock = new();
    private IAsyncDisposable? _lockLease;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    internal PollingConsumer(
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
        long rebuildThreshold = 1000,
        ITenantProcessorLock? tenantProcessorLock = null,
        ConsumerDistributionMode distributionMode = ConsumerDistributionMode.SingleLeader,
        TimeSpan? tenantLockRetryInterval = null,
        int maxParallelProjections = 1,
        ILogger<PollingConsumer>? logger = null)
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
        _tenantProcessorLock = tenantProcessorLock;
        _distributionMode = distributionMode;
        _tenantLockRetryInterval = tenantLockRetryInterval ?? TimeSpan.FromSeconds(30);
        _maxParallelProjections = maxParallelProjections;
        _deadLetterStore = deadLetterStore;
        _pipeline = pipeline;
        _errorPolicy = errorPolicy ?? ErrorPolicy.Default;
        _logger = logger;
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

        // Try to acquire leadership if lock is configured (single-leader mode)
        if (_processorLock is not null)
        {
            _lockLease = await _processorLock.TryAcquireAsync(ConsumerId, ct);
            if (_lockLease is null)
            {
                _logger?.LogInformation(
                    "Consumer {ConsumerId} failed to acquire single-leader lock, running as standby",
                    ConsumerId);
                return;
            }

            _logger?.LogInformation(
                "Consumer {ConsumerId} acquired single-leader lock, processing all tenants",
                ConsumerId);
        }
        else if (_distributionMode == ConsumerDistributionMode.TenantDistributed)
        {
            _logger?.LogInformation(
                "Consumer {ConsumerId} starting in tenant-distributed mode, will acquire tenant locks dynamically",
                ConsumerId);
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

        // Release single-leader lock
        if (_lockLease is not null)
        {
            _logger?.LogInformation(
                "Consumer {ConsumerId} releasing single-leader lock",
                ConsumerId);
            await _lockLease.DisposeAsync();
            _lockLease = null;
        }

        // Release all tenant leases
        if (_tenantLeases.Count > 0)
        {
            _logger?.LogInformation(
                "Consumer {ConsumerId} releasing locks for {TenantCount} tenant(s): [{Tenants}]",
                ConsumerId,
                _tenantLeases.Count,
                string.Join(", ", _tenantLeases.Keys));

            foreach (var kvp in _tenantLeases)
            {
                await kvp.Value.DisposeAsync();
            }
            _tenantLeases.Clear();
        }

        lock (_tenantLock)
        {
            _ownedTenants.Clear();
        }

        _tenantLockCooldowns.Clear();

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
                    // In tenant-distributed mode, filter to events for owned tenants
                    var eventsToProcess = _distributionMode == ConsumerDistributionMode.TenantDistributed
                        ? await FilterEventsByTenantOwnershipAsync(events, ct)
                        : events;

                    // Route to each active processor, filtering by their individual checkpoint
                    // Process in parallel with semaphore to limit concurrency
                    using var semaphore = new SemaphoreSlim(_maxParallelProjections);
                    var processedFlags = new ConcurrentBag<bool>();

                    var tasks = _processors.Select(async processor =>
                    {
                        if (!processor.IsActive || processor.IsRebuilding)
                            return;

                        var processorCheckpoint = activeCheckpoints.GetValueOrDefault(processor.ProcessorId, 0);

                        var relevant = eventsToProcess
                            .Where(e => e.GlobalPosition > processorCheckpoint)
                            .Where(e => processor.HandledEventTypes.Contains(e.EventType.Id))
                            .ToList();

                        if (relevant.Count > 0)
                        {
                            await semaphore.WaitAsync(ct);
                            try
                            {
                                await ProcessEventsForProcessorAsync(processor, relevant, processorCheckpoint, ct);
                                processedFlags.Add(true);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }
                    });

                    await Task.WhenAll(tasks);
                    processedAny = !processedFlags.IsEmpty;

                    // In tenant-distributed mode, advance checkpoints past filtered events
                    // These events will be processed by other consumers that own those tenants
                    if (_distributionMode == ConsumerDistributionMode.TenantDistributed &&
                        eventsToProcess.Count < events.Count)
                    {
                        var maxOriginalPosition = events.Max(e => e.GlobalPosition);
                        await AdvanceCheckpointsPastFilteredEventsAsync(
                            activeCheckpoints, eventsToProcess, maxOriginalPosition, ct);
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

    /// <summary>
    /// Filters events to only those for tenants this instance owns.
    /// Attempts to acquire locks for new tenants encountered.
    /// </summary>
    private async Task<IReadOnlyCollection<IEventEnvelope>> FilterEventsByTenantOwnershipAsync(
        IReadOnlyCollection<IEventEnvelope> events,
        CancellationToken ct)
    {
        if (_tenantProcessorLock is null)
        {
            return events;
        }

        var result = new List<IEventEnvelope>();

        // Group events by tenant
        var eventsByTenant = events.GroupBy(e => e.TenantId);

        var tenantsNotOwned = new List<string>();

        foreach (var tenantGroup in eventsByTenant)
        {
            var tenantId = tenantGroup.Key;
            var ownsLock = await EnsureTenantOwnershipAsync(tenantId, ct);

            if (ownsLock)
            {
                result.AddRange(tenantGroup);
            }
            else
            {
                tenantsNotOwned.Add(tenantId);
            }
        }

        // Record filtered events metric
        var filteredCount = events.Count - result.Count;
        if (filteredCount > 0)
        {
            AlbertoMetrics.EventsFilteredByTenant.Add(filteredCount,
                new KeyValuePair<string, object?>("consumer.id", ConsumerId),
                new KeyValuePair<string, object?>("module.key", _moduleKey));

            _logger?.LogDebug(
                "Consumer {ConsumerId} filtered {FilteredCount} events for {TenantCount} tenants not owned: [{Tenants}]",
                ConsumerId, filteredCount, tenantsNotOwned.Count, string.Join(", ", tenantsNotOwned));
        }

        return result;
    }

    /// <summary>
    /// Advances processor checkpoints past events that were filtered due to tenant ownership.
    /// This allows progress when events at the front of the queue belong to tenants owned by other consumers.
    /// </summary>
    private async Task AdvanceCheckpointsPastFilteredEventsAsync(
        Dictionary<string, long> activeCheckpoints,
        IReadOnlyCollection<IEventEnvelope> processedEvents,
        long maxOriginalPosition,
        CancellationToken ct)
    {
        // For each processor, check if we need to advance its checkpoint past filtered events
        foreach (var processor in _processors)
        {
            if (!processor.IsActive || processor.IsRebuilding)
                continue;

            var currentCheckpoint = activeCheckpoints.GetValueOrDefault(processor.ProcessorId, 0);

            // Find the max position this processor actually processed
            var maxProcessedPosition = processedEvents
                .Where(e => e.GlobalPosition > currentCheckpoint)
                .Where(e => processor.HandledEventTypes.Contains(e.EventType.Id))
                .Select(e => e.GlobalPosition)
                .DefaultIfEmpty(currentCheckpoint)
                .Max();

            // If there were filtered events beyond what we processed, advance the checkpoint
            // This allows progress past events for tenants we don't own
            if (maxOriginalPosition > maxProcessedPosition && maxProcessedPosition >= currentCheckpoint)
            {
                // Only advance to maxOriginalPosition if we didn't process any events,
                // or if all our processed events are below the filtered events
                var newCheckpoint = maxOriginalPosition;

                await _checkpointStore.SaveAsync(processor.ProcessorId, newCheckpoint, ct);

                _logger?.LogDebug(
                    "Consumer {ConsumerId} advanced processor {ProcessorId} checkpoint from {OldCheckpoint} to {NewCheckpoint} (skipped filtered tenant events)",
                    ConsumerId, processor.ProcessorId, currentCheckpoint, newCheckpoint);
            }
        }
    }

    /// <summary>
    /// Ensures this instance owns the lock for the specified tenant.
    /// Returns true if the lock is owned (either already held or just acquired).
    /// Uses cooldown to prevent spamming lock acquisition attempts.
    /// </summary>
    private async Task<bool> EnsureTenantOwnershipAsync(string tenantId, CancellationToken ct)
    {
        // Fast path: check if already owned
        lock (_tenantLock)
        {
            if (_ownedTenants.Contains(tenantId))
            {
                return true;
            }
        }

        // Check cooldown - don't spam lock attempts
        if (_tenantLockCooldowns.TryGetValue(tenantId, out var cooldownUntil))
        {
            if (DateTimeOffset.UtcNow < cooldownUntil)
            {
                // Still in cooldown, skip this cycle
                return false;
            }

            // Cooldown expired, remove and retry
            _tenantLockCooldowns.TryRemove(tenantId, out _);
            _logger?.LogDebug(
                "Consumer {ConsumerId} cooldown expired for tenant {TenantId}, retrying lock acquisition",
                ConsumerId, tenantId);
        }

        // Try to acquire the lock
        if (_tenantProcessorLock is null)
        {
            return true;
        }

        var lease = await _tenantProcessorLock.TryAcquireForTenantAsync(ConsumerId, tenantId, ct);
        if (lease is null)
        {
            // Set cooldown before retrying
            var newCooldownUntil = DateTimeOffset.UtcNow.Add(_tenantLockRetryInterval);
            _tenantLockCooldowns[tenantId] = newCooldownUntil;

            // Record metric
            AlbertoMetrics.TenantLockFailures.Add(1,
                new KeyValuePair<string, object?>("tenant.id", tenantId),
                new KeyValuePair<string, object?>("consumer.id", ConsumerId));

            _logger?.LogDebug(
                "Consumer {ConsumerId} failed to acquire lock for tenant {TenantId}, cooldown until {CooldownUntil}",
                ConsumerId, tenantId, newCooldownUntil);

            return false;
        }

        // Track ownership
        _tenantLeases[tenantId] = lease;

        lock (_tenantLock)
        {
            _ownedTenants.Add(tenantId);
        }

        // Record metric
        AlbertoMetrics.TenantLocksAcquired.Add(1,
            new KeyValuePair<string, object?>("tenant.id", tenantId),
            new KeyValuePair<string, object?>("consumer.id", ConsumerId));

        _logger?.LogInformation(
            "Consumer {ConsumerId} acquired lock for tenant {TenantId}. Now processing {TenantCount} tenant(s): [{Tenants}]",
            ConsumerId,
            tenantId,
            _ownedTenants.Count,
            string.Join(", ", _ownedTenants));

        return true;
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

        // Record tenant ownership metrics (only in tenant-distributed mode)
        if (_distributionMode == ConsumerDistributionMode.TenantDistributed)
        {
            int ownedCount;
            lock (_tenantLock)
            {
                ownedCount = _ownedTenants.Count;
            }
            AlbertoMetrics.RecordTenantOwnership(ConsumerId, _moduleKey, ownedCount, _tenantLockCooldowns.Count);
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
