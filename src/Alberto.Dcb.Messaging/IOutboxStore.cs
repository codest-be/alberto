namespace Alberto.Dcb.Messaging;

/// <summary>
/// Persistent store for outbox entries. Implementations are responsible for
/// durably writing entries and retrieving pending messages for relay.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Inserts a new outbox entry. Duplicate source events are ignored.</summary>
    Task InsertAsync(OutboxEntry entry, CancellationToken ct = default);

    /// <summary>Returns up to <paramref name="limit"/> pending entries ordered by creation time.</summary>
    Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>Marks an entry as successfully delivered.</summary>
    Task MarkDeliveredAsync(Guid id, CancellationToken ct = default);

    /// <summary>Marks an entry as failed, incrementing the retry counter and recording the error.</summary>
    Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default);

    /// <summary>
    /// Resets failed entries back to pending so the relay will attempt delivery again.
    /// When <paramref name="messageType"/> is provided, only entries of that type are reset.
    /// </summary>
    Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default);

    /// <summary>Permanently removes delivered entries older than <paramref name="before"/>.</summary>
    Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default);
}
