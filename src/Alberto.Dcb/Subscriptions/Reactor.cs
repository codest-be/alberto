namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Base class for reactors - processors that perform side effects in response to events.
/// Implement IReact&lt;TEvent&gt; for each event type you handle.
/// </summary>
/// <example>
/// <code>
/// public class OrderNotificationReactor : Reactor,
///     IReact&lt;OrderConfirmed&gt;
/// {
///     private readonly IEmailService _emailService;
///
///     public OrderNotificationReactor(ICheckpointStore checkpointStore, IEmailService emailService)
///         : base(checkpointStore)
///     {
///         _emailService = emailService;
///     }
///
///     public override string ProcessorId => "order-notifications";
///
///     public async Task ReactAsync(OrderConfirmed e, IEventEnvelope envelope, CancellationToken ct)
///     {
///         await _emailService.SendConfirmationAsync(e.OrderId, envelope.TenantId);
///     }
/// }
/// </code>
/// </example>
public abstract class Reactor : IEventProcessor
{
    private readonly ICheckpointStore _checkpointStore;
    private readonly ReactorDispatcher _dispatcher;

    protected Reactor(ICheckpointStore checkpointStore)
    {
        _checkpointStore = checkpointStore;
        _dispatcher = ReactorDispatcher.For(this);
    }

    /// <summary>
    /// Unique identifier for this processor. Used as the checkpoint key.
    /// </summary>
    public abstract string ProcessorId { get; }

    /// <summary>
    /// Whether this processor is currently active.
    /// </summary>
    public virtual bool IsActive => true;

    /// <summary>
    /// The event types this processor handles.
    /// </summary>
    public IReadOnlySet<string> HandledEventTypes => _dispatcher.HandledEventTypes;

    /// <summary>
    /// Process a batch of events.
    /// Events are processed sequentially (side effects often need ordering).
    /// </summary>
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
