namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Polling-based event consumer that routes events from the store to processors.
/// Supports optional leader election for single-instance processing.
/// </summary>
public sealed class PollingConsumer : IEventConsumer
{
    private readonly IEventStoreBackend _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly IProcessorLock? _processorLock;

    private readonly List<IEventProcessor> _processors = [];
    private IAsyncDisposable? _lockLease;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public PollingConsumer(
        IEventStoreBackend eventStore,
        ICheckpointStore checkpointStore,
        string consumerId,
        TimeSpan? pollingInterval = null,
        int batchSize = 100,
        IProcessorLock? processorLock = null)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        ConsumerId = consumerId ?? throw new ArgumentNullException(nameof(consumerId));
        _pollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(100);
        _batchSize = batchSize;
        _processorLock = processorLock;
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
                            await processor.ProcessBatchAsync(relevant, ct);
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
