namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal processor that wraps a reactor instance for async processing.
/// The reactor should implement IReact&lt;TEvent&gt; interfaces - no base class needed.
/// </summary>
internal sealed class AsyncReactor<TReactor> : IEventProcessor
    where TReactor : class
{
    private readonly TReactor _reactor;
    private readonly ReactorDispatcher _dispatcher;

    public AsyncReactor(TReactor reactor, string processorId)
    {
        _reactor = reactor ?? throw new ArgumentNullException(nameof(reactor));
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
    public bool IsActive { get; set; } = true;

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _dispatcher.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        if (_dispatcher.CanHandle(@event.EventType.Id))
        {
            await _dispatcher.ReactAsync(@event, ct);
        }
    }
}
