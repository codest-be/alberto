namespace Alberto.Messaging;

/// <summary>
/// Persistent store for outbox entries. Implementations are responsible for
/// durably writing entries and retrieving pending messages for relay.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Inserts a new outbox entry. Duplicate source events are ignored.</summary>
    Task InsertAsync(OutboxEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims up to <paramref name="limit"/> deliverable entries, ordered by creation time.
    /// Pending entries and processing entries whose claim lease expired are eligible.
    /// </summary>
    Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
        int limit,
        TimeSpan claimLease,
        string claimedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a claimed entry as successfully delivered.
    /// Returns <see langword="false"/> when the claim expired or was superseded.
    /// </summary>
    Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks several claimed entries as delivered and returns the ids of those it marked; an
    /// entry whose claim expired or was superseded is left out. The default implementation calls
    /// <see cref="MarkDeliveredAsync"/> once per claim, so a store that predates this method keeps
    /// working; override it to mark the batch in one round trip.
    /// </summary>
    async Task<IReadOnlySet<Guid>> MarkDeliveredBatchAsync(
        IReadOnlyList<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        var marked = new HashSet<Guid>();
        foreach (var claim in claims)
            if (await MarkDeliveredAsync(claim, cancellationToken))
                marked.Add(claim.Entry.Id);
        return marked;
    }

    /// <summary>
    /// Marks a claimed entry as failed, incrementing the retry counter and recording the error.
    /// The entry is parked as <c>failed</c> immediately (equivalent to <c>maxRetries</c> of 1).
    /// Returns <see langword="false"/> when the claim expired or was superseded.
    /// </summary>
    Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed delivery attempt: increments the retry counter and records the error.
    /// The entry returns to <c>pending</c> for another attempt until the incremented counter
    /// reaches <paramref name="maxRetries"/>; that failure parks it as <c>failed</c>.
    /// Returns <see langword="false"/> when the claim expired or was superseded.
    /// The default implementation parks the entry on the first failure, as <see cref="MarkFailedAsync"/>
    /// does, so a store that predates retries keeps working; override it to get retries.
    /// </summary>
    Task<bool> MarkAttemptFailedAsync(OutboxClaim claim, string error, int maxRetries,
        CancellationToken cancellationToken) => MarkFailedAsync(claim, error, cancellationToken);

    /// <summary>
    /// Resets failed entries back to pending so the relay will attempt delivery again.
    /// When <paramref name="messageType"/> is provided, only entries of that type are reset.
    /// </summary>
    Task RetryFailedAsync(string? messageType = null, CancellationToken cancellationToken = default);

    /// <summary>Permanently removes delivered entries older than <paramref name="before"/>.</summary>
    Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}
