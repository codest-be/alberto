namespace Alberto.Projections;

/// <summary>
/// Projection that runs inside <see cref="IEventStore.AppendAsync"/>: after the events are inserted,
/// under the backend's append lock, before they become visible. The events and the projection commit
/// together, in log order, and the caller reads its own writes as soon as the append returns.
/// </summary>
/// <remarks>
/// Implementations process only events whose type is in <see cref="HandledEventTypes"/>; the event
/// store filters before invoking <see cref="ProcessAsync"/>. A failure aborts the append: the caller
/// sees the exception and the events were not written.
/// <para>
/// The Postgres backends give the projection the append's own database transaction, so its writes
/// roll back with the events. The in-memory backend has no database transaction and hands over
/// <see cref="AppendTransaction.None"/>: the projection runs under the backend's append lock,
/// before the events become visible, and a failure discards them.
/// A projection that appends to the same in-memory backend from inside this method waits on that lock forever.
/// </para>
/// <para>
/// Work that must happen only after the events are committed, such as publishing a message or calling
/// another system, belongs in an <see cref="Subscriptions.IPostAppendHandler"/>.
/// </para>
/// </remarks>
public interface IInlineProjection
{
    /// <summary>
    /// The event types this projection handles.
    /// </summary>
    IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>
    /// Processes events inside the append's transaction.
    /// </summary>
    /// <param name="events">The events to process, in log order.</param>
    /// <param name="transaction">The append's connection and uncommitted transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        AppendTransaction transaction,
        CancellationToken cancellationToken = default);
}
