namespace Alberto.Dcb.Messaging;

/// <summary>
/// Persistent store for outbox entries. Implementations are responsible for
/// durably writing entries and retrieving pending messages for relay.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Inserts a new outbox entry. Duplicate source events are ignored.</summary>
    Task InsertAsync(OutboxEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims up to <paramref name="limit"/> deliverable entries, ordered by creation time.
    /// Pending entries and processing entries whose claim lease expired are eligible.
    /// </summary>
    Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
        int limit,
        TimeSpan claimLease,
        string claimedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a claimed entry as successfully delivered.
    /// Returns <see langword="false"/> when the claim expired or was superseded.
    /// </summary>
    Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default);

    /// <summary>
    /// Marks a claimed entry as failed, incrementing the retry counter and recording the error.
    /// Returns <see langword="false"/> when the claim expired or was superseded.
    /// </summary>
    Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default);

    /// <summary>
    /// Resets failed entries back to pending so the relay will attempt delivery again.
    /// When <paramref name="messageType"/> is provided, only entries of that type are reset.
    /// </summary>
    Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default);

    /// <summary>Permanently removes delivered entries older than <paramref name="before"/>.</summary>
    Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default);
}
