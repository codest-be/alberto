using System.Collections.Concurrent;
using Alberto.Subscriptions;

namespace Alberto.InMemory;

/// <summary>
/// In-memory implementation of dead letter storage.
/// Useful for testing and development.
/// </summary>
/// <param name="timeProvider">Clock used to stamp <see cref="DeadLetterEntry.CreatedAt"/> when not supplied by the caller, and to drive claim-lease expiry. Defaults to <see cref="TimeProvider.System"/>.</param>
public sealed class InMemoryDeadLetterStore(TimeProvider? timeProvider = null) : IClaimableDeadLetterStore
{
    // Keyed by (processor, event): the store holds one entry per event a processor failed.
    private readonly ConcurrentDictionary<(string ProcessorId, Guid EventId), DeadLetterEntry> _entries = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        var stamped = entry with { CreatedAt = entry.CreatedAt ?? _timeProvider.GetUtcNow().UtcDateTime };

        // A repeat failure refreshes the existing entry but keeps its id, retry flag and claim,
        // so a worker retrying it still owns the row it claimed.
        _entries.AddOrUpdate(
            Key(entry),
            stamped,
            (_, existing) => stamped with
            {
                Id = existing.Id,
                RetryRequested = existing.RetryRequested,
                ClaimedAt = existing.ClaimedAt,
                ClaimExpiresAt = existing.ClaimExpiresAt,
                ClaimedBy = existing.ClaimedBy,
                ClaimId = existing.ClaimId,
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        string? tenantId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var entries = _entries.Values
            .Where(e => e.ProcessorId == processorId)
            // Invariant: e.TenantId is null only for single-tenant entries. Multi-tenant events
            // are always stamped with a non-null TenantId by the tenant decorator before append,
            // and DeadLetterEntryFactory.Create copies envelope.TenantId verbatim. The
            // `e.TenantId is null` clause therefore matches only single-tenant entries and
            // never leaks entries across tenants in a multi-tenant store.
            .Where(e => tenantId is null || e.TenantId is null || e.TenantId == tenantId)
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
    public Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var key = Key(claim.Entry);
        while (_entries.TryGetValue(key, out var existing))
        {
            if (!OwnsClaim(existing, claim))
                return Task.FromResult(false);

            if (_entries.TryRemove(new KeyValuePair<(string, Guid), DeadLetterEntry>(key, existing)))
                return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task ClearAsync(string processorId, CancellationToken ct = default)
    {
        var toRemove = _entries.Keys.Where(k => k.ProcessorId == processorId).ToList();
        foreach (var id in toRemove)
        {
            _entries.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
    {
        foreach (var key in _entries.Keys.Where(k => k.ProcessorId == processorId))
        {
            // CAS so a concurrent refresh or claim is not overwritten with a stale copy.
            while (_entries.TryGetValue(key, out var entry)
                   && !_entries.TryUpdate(key, entry with { RetryRequested = true }, entry))
            {
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        CancellationToken ct = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");

        var now = _timeProvider.GetUtcNow();
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

        var claimed = new List<DeadLetterClaim>();
        foreach (var original in candidates)
        {
            var claimId = Guid.NewGuid();
            var updated = original with
            {
                ClaimedAt = now,
                ClaimExpiresAt = lease,
                ClaimedBy = claimedBy,
                ClaimId = claimId,
            };
            if (_entries.TryUpdate(Key(original), updated, original))
                claimed.Add(new DeadLetterClaim(updated, claimId, lease));
        }

        return Task.FromResult<IReadOnlyList<DeadLetterClaim>>(claimed);
    }

    /// <inheritdoc />
    public Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var key = Key(claim.Entry);
        while (_entries.TryGetValue(key, out var existing))
        {
            if (!OwnsClaim(existing, claim))
                return Task.FromResult(false);

            var abandoned = existing with
            {
                RetryRequested = false,
                ClaimedAt = null,
                ClaimExpiresAt = null,
                ClaimedBy = null,
                ClaimId = null,
            };

            if (_entries.TryUpdate(key, abandoned, existing))
                return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private static bool OwnsClaim(DeadLetterEntry existing, DeadLetterClaim claim) =>
        existing.Id == claim.Entry.Id && existing.ClaimId == claim.Token;

    private static (string, Guid) Key(DeadLetterEntry entry) => (entry.ProcessorId, entry.EventId);
}
