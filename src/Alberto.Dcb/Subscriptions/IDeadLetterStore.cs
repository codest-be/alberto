namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Storage for events that failed processing.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>
    /// Stores a failed event in dead letter storage.
    /// </summary>
    Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Gets dead letter entries for a processor.
    /// </summary>
    /// <param name="processorId">The processor identifier.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the count of dead letter entries for a processor.
    /// </summary>
    Task<int> CountAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Removes a dead letter entry (e.g., after successful replay).
    /// </summary>
    Task RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Removes all dead letter entries for a processor.
    /// </summary>
    Task ClearAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Marks dead letter entries for retry via CLI. Sets retry_requested flag for reprocessing.
    /// </summary>
    Task MarkForRetryAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims dead letter entries marked for retry, holding them with a time-bounded lease.
    /// While the lease is active no other worker will claim the same row; if the worker holding the
    /// claim dies before deleting (success) or releasing (failure) the row, the lease expires and the
    /// row becomes available for re-claim. Replaces the previous "delete-before-dispatch" approach,
    /// which lost events on worker crash mid-dispatch.
    /// </summary>
    /// <param name="processorId">The processor identifier.</param>
    /// <param name="batchSize">Maximum entries to claim.</param>
    /// <param name="leaseDuration">How long the claim is valid; should exceed the longest expected handler runtime.</param>
    /// <param name="claimedBy">Identifier of the claiming worker (e.g. replica id), recorded for diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DeadLetterEntry>> ClaimRetryRequestedAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Releases an active claim on a dead letter entry without deleting it, making it eligible for
    /// immediate re-claim. Used when a worker fails to make progress and wants to hand the entry back.
    /// No-op if the entry is gone or no longer claimed.
    /// </summary>
    Task ReleaseClaimAsync(Guid id, CancellationToken ct = default);
}
