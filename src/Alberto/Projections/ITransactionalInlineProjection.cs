namespace Alberto.Projections;

/// <summary>
/// An <see cref="IInlineProjection"/> that can write inside the append's own transaction, so the
/// events and the projection commit together, in log order, under the append lock.
/// </summary>
/// <remarks>
/// The Postgres backends give the projection the append's own database transaction; there a failure
/// rolls the append back, the caller sees the exception and nothing was written. The in-memory backend has
/// no database transaction and hands over <see cref="AppendTransaction.None"/>: the projection runs under
/// the backend's append lock, before the events become visible, and a failure discards them. Either way
/// it sees events in log order.
/// A backend that is not an <see cref="ITransactionalEventStoreBackend"/>, or reports
/// <see cref="ITransactionalEventStoreBackend.SupportsAppendTransaction"/> false, falls back to
/// <see cref="IInlineProjection.ProcessAsync"/> after the append commits, with no ordering.
/// A projection that appends to the same in-memory backend from inside this method waits on that lock forever.
/// </remarks>
public interface ITransactionalInlineProjection : IInlineProjection
{
    /// <summary>
    /// Processes events inside the append's transaction.
    /// </summary>
    /// <param name="events">The events to process.</param>
    /// <param name="transaction">The append's connection and uncommitted transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProcessInTransactionAsync(
        IReadOnlyList<IEventEnvelope> events,
        AppendTransaction transaction,
        CancellationToken cancellationToken = default);
}
