using System.Data;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal interface for inline projections that run in the same transaction as event commit.
/// Use <see cref="IEventStore.RegisterInlineProjection{TState, TProjection}"/> to register projections.
/// </summary>
internal interface IInlineProjection
{
    /// <summary>
    /// The event types this projection handles.
    /// </summary>
    IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>
    /// Processes events within the append transaction.
    /// </summary>
    /// <param name="events">The events to process.</param>
    /// <param name="transaction">The database transaction from the append operation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        IDbTransaction transaction,
        CancellationToken ct = default);
}
