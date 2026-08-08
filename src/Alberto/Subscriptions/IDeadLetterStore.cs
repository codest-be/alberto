namespace Alberto.Subscriptions;

/// <summary>
/// Storage for events that failed processing.
/// </summary>
/// <remarks>
/// This is the surface every dead-letter store must provide: record a failure, read it back,
/// count it, clear it, and flag it for retry. Automatic retry — claiming an entry under a
/// time-bounded lease so exactly one worker dispatches it — is a separate capability on
/// <see cref="IClaimableDeadLetterStore"/>, because implementing it correctly needs atomic
/// claim-and-fence semantics that not every backing store can offer. A store that cannot
/// implement those three methods correctly should implement this interface only, rather
/// than supplying versions that look right and lose events under contention.
/// </remarks>
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
    /// <remarks>
    /// <para>
    /// <strong>Single-tenant stores.</strong> In a single-tenant deployment entries are stored
    /// without a tenant identifier, so their stored <see cref="DeadLetterEntry.TenantId"/> is
    /// <see langword="null"/>. Passing a non-null <paramref name="tenantId"/> to a store that
    /// holds only unscoped entries is a no-op: every implementation must return those entries
    /// regardless, because the argument is meaningless when the store has no tenant column to
    /// filter on. A caller that passes a tenant ID is entitled to ask "these events for this
    /// tenant"; if the store has no tenancy, the most recent dead-letter events are the same
    /// list no matter which tenant is named. Filtering them out would make the CLI return
    /// nothing on a single-tenant deployment while Postgres returns the full list.
    /// </para>
    /// <para>
    /// <strong>Multi-tenant stores.</strong> Every entry is stored with the non-null tenant
    /// that owned the event; passing a non-null <paramref name="tenantId"/> filters to that
    /// tenant only, and <see langword="null"/> returns entries for all tenants (the cross-tenant
    /// operator view).
    /// </para>
    /// </remarks>
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
    /// Removes all dead letter entries for a processor.
    /// </summary>
    Task ClearAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Marks dead letter entries for retry via CLI. Sets retry_requested flag for reprocessing.
    /// </summary>
    /// <remarks>
    /// Marking is not dispatching. Entries flagged here are picked up by
    /// <see cref="DeadLetterRetryLoop"/>, which requires the store to also implement
    /// <see cref="IClaimableDeadLetterStore"/>. On a store that does not, the flag is
    /// recorded and nothing acts on it — the operator must drive the retry themselves.
    /// </remarks>
    Task MarkForRetryAsync(string processorId, CancellationToken ct = default);
}

/// <summary>
/// A dead letter store that can hand an entry to exactly one worker at a time, so failed
/// events can be retried automatically without two replicas dispatching the same event.
/// </summary>
/// <remarks>
/// <para>
/// The three methods here form one protocol and only make sense together: claim an entry under
/// a lease, then either complete it (the retry succeeded — remove the row) or abandon it (the
/// retry failed again — leave the row but stop scheduling it). A worker that dies between claim
/// and either outcome is handled by lease expiry, not by a fourth method.
/// </para>
/// <para>
/// Automatic retry is opt-in at the store level for a reason: it needs an atomic
/// claim-and-fence that a store without row-level locking cannot provide. Rather than push
/// every implementor into writing a version that appears to work and duplicates dispatches
/// under contention, a store simply declares whether it can do this.
/// </para>
/// </remarks>
public interface IClaimableDeadLetterStore : IDeadLetterStore
{
    /// <summary>
    /// Completes a successful retry by removing the entry only if
    /// <paramref name="claim"/> still owns the row. An expired token remains valid until
    /// another worker fences it by reclaiming the row.
    /// </summary>
    /// <returns><see langword="true"/> when the claimed row was removed.</returns>
    Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default);

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
    /// busy-loop. The entry must be explicitly re-marked via <see cref="IDeadLetterStore.MarkForRetryAsync"/> to
    /// retry again. Worker crashes are handled by lease expiry, not this method.
    /// </summary>
    /// <returns><see langword="true"/> when the active claim was abandoned.</returns>
    Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default);
}
