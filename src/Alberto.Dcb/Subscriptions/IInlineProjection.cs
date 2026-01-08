using System.Data;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal interface for inline projections that run immediately after event append.
/// Use <see cref="IEventStore.RegisterInlineProjection{TState, TProjection}"/> to register projections.
/// </summary>
internal interface IInlineProjection
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
