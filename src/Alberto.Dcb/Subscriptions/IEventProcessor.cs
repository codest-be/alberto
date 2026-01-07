namespace Alberto.Dcb.Subscriptions;

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
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// The event types this processor handles.
    /// Used by consumers to filter events before delivering them.
    /// </summary>
    IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>
    /// Process a batch of events.
    /// The processor is responsible for saving its checkpoint after successful processing.
    /// </summary>
    Task<ProcessingResult> ProcessBatchAsync(
        IReadOnlyList<IEventEnvelope> events,
        CancellationToken ct = default);
}
