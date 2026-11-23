namespace Alberto.Projections;

/// <summary>
/// An <see cref="IInlineProjection"/> that can write inside the append's own transaction, so the
/// events and the projection commit together, in log order, under the append lock.
/// </summary>
/// <remarks>
/// On a backend that is not an <see cref="ITransactionalEventStoreBackend"/> the event store falls back to
/// <see cref="IInlineProjection.ProcessAsync"/> after the append commits. Inside the transaction a
/// failure rolls the append back: the caller sees the exception and nothing was written.
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
