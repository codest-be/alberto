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
    /// <param name="tenantId">
    /// When supplied, restricts the result to entries belonging to that tenant. Dead letter
    /// entries carry the full event payload, so a multi-tenant caller must pass the active
    /// tenant to avoid disclosing another tenant's data. Pass <see langword="null"/> for the
    /// cross-tenant operator view (the CLI) and in single-tenant deployments.
    /// </param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        string? tenantId = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the count of dead letter entries for a processor.
    /// </summary>
    Task<int> CountAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Completes a successful retry by removing the entry only if
    /// <paramref name="claim"/> still owns the row. An expired token remains valid until
    /// another worker fences it by reclaiming the row.
    /// </summary>
    /// <returns><see langword="true"/> when the claimed row was removed.</returns>
    Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default);

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
    Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Abandons a retry attempt, if <paramref name="claim"/> still owns it: clears
    /// <c>retry_requested</c> and the claim columns so the entry
    /// stays in the dead letter table but is no longer scheduled for automatic retry. Used when a
    /// dispatch throws — the handler is still failing, so re-running it on the next poll would just
    /// busy-loop. The entry must be explicitly re-marked via <see cref="MarkForRetryAsync"/> to
    /// retry again. Worker crashes are handled by lease expiry, not this method.
    /// </summary>
    /// <returns><see langword="true"/> when the active claim was abandoned.</returns>
    Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default);
}
