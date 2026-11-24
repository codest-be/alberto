namespace Alberto;

/// <summary>
/// What a backend author implements: the whole of <see cref="IEventStore"/> (append, batch reads,
/// streaming reads, last position) plus the type-filtered read the control loop needs.
/// Application code depends on <see cref="IEventStore"/>; <see cref="EventStore"/> wraps a backend
/// and adds argument validation, inline projections and post-append handlers on top.
/// A backend must be able to run work inside its own append (<see cref="AppendAsync(IEnumerable{IEventToPersist}, DcbQuery?, long?, Func{IReadOnlyList{IEventEnvelope}, AppendTransaction, CancellationToken, Task}, CancellationToken)"/>):
/// that is what makes an inline projection atomic with the events and ordered by the log.
/// In single-tenant mode (default), no tenant scoping is applied.
/// In multi-tenant mode (opt-in via .WithTenancy()), tenant scoping is handled by the decorator.
/// </summary>
public interface IEventStoreBackend : IEventStore
{
    /// <summary>
    /// As <see cref="IEventStore.ReadAllAsync"/>, for a reader that only wants <paramref name="eventTypes"/>,
    /// so a backend can leave the other events on the server.
    /// </summary>
    /// <remarks>
    /// Implementations must filter: only events of <paramref name="eventTypes"/> come back, in
    /// position order, and fewer than <paramref name="limit"/> means no further event of these
    /// types was visible after <paramref name="afterPosition"/>.
    /// </remarks>
    Task<IReadOnlyCollection<IEventEnvelope>> ReadAllOfTypesAsync(
        IReadOnlySet<string> eventTypes,
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="IEventStore.AppendAsync"/>, and after the events are inserted, still inside the
    /// transaction and while the backend's append lock is held, awaits <paramref name="inTransaction"/>.
    /// If it throws, the append is rolled back (or, without a database transaction, never published)
    /// and the exception propagates; otherwise the transaction commits.
    /// </summary>
    /// <remarks>
    /// A backend with a database transaction hands over its connection and transaction in the
    /// <see cref="AppendTransaction"/>. A backend with none holds its append lock, hands over
    /// <see cref="AppendTransaction.None"/>, runs the work before the events become visible and discards
    /// them if it throws. The in-memory backend does this.
    /// </remarks>
    Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        Func<IReadOnlyList<IEventEnvelope>, AppendTransaction, CancellationToken, Task> inTransaction,
        CancellationToken cancellationToken = default);
}
