using System.Data;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Inline projection that runs immediately after event append, before <see cref="IEventStore.AppendAsync"/>
/// returns to the caller. Used to provide read-your-writes consistency for projections that the
/// caller will query immediately after the mutation.
/// </summary>
/// <remarks>
/// Implementations process only events whose type is in <see cref="HandledEventTypes"/>; the event
/// store filters before invoking <see cref="ProcessAsync"/>. Failures propagate to the caller and
/// fail the mutation — but the events themselves are already committed by the time inline
/// projections run, so a projection failure is observable as a 500 with the event persisted.
/// </remarks>
public interface IInlineProjection
{
    /// <summary>
    /// The event types this projection handles.
    /// </summary>
    IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>
    /// Processes events after they have been appended.
    /// </summary>
    /// <param name="events">The events to process.</param>
    /// <param name="transaction">Optional database transaction for state store operations.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        IDbTransaction? transaction = null,
        CancellationToken ct = default);
}
