namespace Alberto.Subscriptions;

/// <summary>
/// Base interface for event processors (Projectors, Reactors, etc.).
/// Processors handle events and manage their own checkpoints.
/// </summary>
public interface IEventProcessor
{
    /// <summary>
    /// Unique identifier for this processor. Used as the checkpoint key.
    /// Should be versioned (e.g., "order-summary-v1") to allow rebuilding.
    /// </summary>
    string ProcessorId { get; }

    /// <summary>
    /// Whether this processor is currently active and should receive events.
    /// Observable by external code; mutated only by the framework via
    /// <see cref="IProcessorLifecycle"/>.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Whether this processor is currently rebuilding (catching up from behind).
    /// Rebuilding processors run independently and don't block other processors.
    /// Observable by external code; mutated only by the framework via
    /// <see cref="IProcessorLifecycle"/>.
    /// </summary>
    bool IsRebuilding { get; }

    /// <summary>
    /// The event types this processor handles.
    /// Used by consumers to filter events before delivering them.
    /// </summary>
    IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>
    /// Process a single event.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default);

}
