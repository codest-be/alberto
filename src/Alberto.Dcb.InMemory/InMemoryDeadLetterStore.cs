using System.Collections.Concurrent;
using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of dead letter storage.
/// Useful for testing and development.
/// </summary>
public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentDictionary<Guid, DeadLetterEntry> _entries = new();

    /// <inheritdoc />
    public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        int limit = 100,
        CancellationToken ct = default)
    {
        var entries = _entries.Values
            .Where(e => e.ProcessorId == processorId)
            .OrderByDescending(e => e.FailedAt)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(entries);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(string processorId, CancellationToken ct = default)
    {
        var count = _entries.Values.Count(e => e.ProcessorId == processorId);
        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        _entries.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(string processorId, CancellationToken ct = default)
    {
        var toRemove = _entries.Where(e => e.Value.ProcessorId == processorId).Select(e => e.Key).ToList();
        foreach (var id in toRemove)
        {
            _entries.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
    {
        foreach (var entry in _entries.Values.Where(e => e.ProcessorId == processorId))
            _entries[entry.Id] = entry with { RetryRequested = true };

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterEntry>> ClaimRetryRequestedAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        CancellationToken ct = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");

        var now = DateTimeOffset.UtcNow;
        var lease = now + leaseDuration;

        // Select-and-stamp under the dictionary's atomic per-key swap. Two
        // concurrent claims for the same row will both see the same snapshot,
        // so we use TryUpdate with the original entry as the comparand to
        // make the claim CAS-style.
        var candidates = _entries.Values
            .Where(e => e.ProcessorId == processorId
                        && e.RetryRequested
                        && (e.ClaimExpiresAt is null || e.ClaimExpiresAt < now))
            .OrderBy(e => e.FailedAt)
            .Take(batchSize)
            .ToList();

        var claimed = new List<DeadLetterEntry>();
        foreach (var original in candidates)
        {
            var updated = original with
            {
                ClaimedAt = now,
                ClaimExpiresAt = lease,
                ClaimedBy = claimedBy,
            };
            if (_entries.TryUpdate(original.Id, updated, original))
                claimed.Add(updated);
        }

        return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(claimed);
    }

    /// <inheritdoc />
    public Task ReleaseClaimAsync(Guid id, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(id, out var existing))
        {
            var released = existing with { ClaimedAt = null, ClaimExpiresAt = null, ClaimedBy = null };
            _entries.TryUpdate(id, released, existing);
        }
        return Task.CompletedTask;
    }
}
