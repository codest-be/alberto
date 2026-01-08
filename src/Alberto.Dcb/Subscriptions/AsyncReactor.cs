namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal processor that wraps a reactor instance for async processing.
/// The reactor should implement IReact&lt;TEvent&gt; interfaces - no base class needed.
/// </summary>
internal sealed class AsyncReactor<TReactor> : IEventProcessor
    where TReactor : class
{
    private readonly TReactor _reactor;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ReactorDispatcher _dispatcher;

    public AsyncReactor(
        TReactor reactor,
        ICheckpointStore checkpointStore,
        string processorId)
    {
        _reactor = reactor ?? throw new ArgumentNullException(nameof(reactor));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        ProcessorId = processorId ?? throw new ArgumentNullException(nameof(processorId));
        _dispatcher = ReactorDispatcher.For(reactor);

        if (_dispatcher.HandledEventTypes.Count == 0)
        {
            throw new ArgumentException(
                $"Reactor type '{typeof(TReactor).Name}' does not implement any IReact<TEvent> interfaces.",
                nameof(reactor));
        }
    }

    /// <inheritdoc/>
    public string ProcessorId { get; }

    /// <inheritdoc/>
    public bool IsActive => true;

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _dispatcher.HandledEventTypes;

    /// <inheritdoc/>
    public async Task<ProcessingResult> ProcessBatchAsync(
        IReadOnlyList<IEventEnvelope> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0)
            return ProcessingResult.Continue;

        foreach (var envelope in events)
        {
            // Only process events we have handlers for
            if (_dispatcher.CanHandle(envelope.EventType.Id))
            {
                await _dispatcher.ReactAsync(envelope, ct);
            }
        }

        // Save checkpoint after successful processing
        var lastPosition = events[^1].GlobalPosition;
        await _checkpointStore.SaveAsync(ProcessorId, lastPosition, ct);

        return ProcessingResult.Continue;
    }
}
