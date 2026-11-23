namespace Alberto;

/// <summary>
/// A backend that can run work inside the transaction that appends the events, while its append
/// lock is held, so that work is atomic with the append and sees events in log order.
/// </summary>
/// <remarks>
/// Optional: <see cref="EventStore"/> runs <see cref="Projections.ITransactionalInlineProjection"/>s through
/// this when <see cref="SupportsAppendTransaction"/> is true and after the commit otherwise.
/// </remarks>
public interface ITransactionalEventStoreBackend : IEventStoreBackend
{
    /// <summary>Whether <see cref="AppendAsync(IEnumerable{IEventToPersist}, DcbQuery?, long?, Func{IReadOnlyList{IEventEnvelope}, AppendTransaction, CancellationToken, Task}, CancellationToken)"/> can be used.</summary>
    bool SupportsAppendTransaction { get; }

    /// <summary>
    /// As <see cref="IEventStore.AppendAsync"/>, and after the events are inserted, still inside the
    /// transaction, awaits <paramref name="inTransaction"/>. If it throws, the append is rolled back
    /// and the exception propagates; otherwise the transaction commits.
    /// </summary>
    Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        Func<IReadOnlyList<IEventEnvelope>, AppendTransaction, CancellationToken, Task> inTransaction,
        CancellationToken cancellationToken = default);
}
