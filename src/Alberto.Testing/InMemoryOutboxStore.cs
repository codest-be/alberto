using Alberto.Messaging;

namespace Alberto.Testing;

/// <summary>
/// A fully in-memory <see cref="IOutboxStore"/> for use in tests.
/// </summary>
/// <remarks>
/// <para>
/// This store faithfully reproduces the observable contract of the PostgreSQL implementation:
/// entries move through <c>pending → processing → delivered / failed</c>; claims are
/// token-fenced so a relay whose lease expired cannot overwrite the result of a newer relay
/// that reclaimed the same entry; and <see cref="ClaimPendingAsync"/> re-claims any
/// <c>processing</c> entry whose lease has passed.
/// </para>
/// <para>
/// All timestamps are read from the injected <see cref="TimeProvider"/> rather than from
/// <see cref="DateTimeOffset.UtcNow"/>, so lease-expiry behaviour is fully exercisable
/// without sleeping.
/// </para>
/// <para>
/// This class is thread-safe. All mutations are protected by a single lock.
/// </para>
/// </remarks>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    // Per-entry mutable state that lives alongside the (immutable record) OutboxEntry.
    private sealed class EntryState(OutboxEntry entry)
    {
        public OutboxEntry Entry { get; set; } = entry;

        /// <summary>Token of the current holder. <see cref="Guid.Empty"/> when not claimed.</summary>
        public Guid ClaimToken { get; set; } = Guid.Empty;

        /// <summary>When the current claim lease expires. Meaningless when not processing.</summary>
        public DateTimeOffset ClaimedUntil { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<Guid, EntryState> _entries = new();

    // Keyed by SourceEventId to enforce the duplicate-source-event rule.
    private readonly HashSet<Guid> _sourceEventIds = new();

    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new store.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock used for claim leases, delivered timestamps, and purge cut-offs.
    /// Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a <c>FakeTimeProvider</c> to control time in tests.
    /// </param>
    public InMemoryOutboxStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// All entries currently held by the store, in insertion order.
    /// </summary>
    /// <remarks>
    /// Each item reflects the entry's current state (status, retry count, timestamps).
    /// The list is a snapshot — it does not update as the store changes.
    /// </remarks>
    public IReadOnlyList<OutboxEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Select(s => s.Entry).ToList();
            }
        }
    }

    /// <inheritdoc/>
    public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (!_sourceEventIds.Add(entry.SourceEventId))
                return Task.CompletedTask; // duplicate source event — silently ignored

            _entries[entry.Id] = new EntryState(entry);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
        int limit,
        TimeSpan claimLease,
        string claimedBy,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var results = new List<OutboxClaim>();

        lock (_lock)
        {
            // Eligible: pending, OR processing with an expired (or missing) lease.
            // Order by creation time to match the PostgreSQL implementation.
            var eligible = _entries.Values
                .Where(s =>
                    s.Entry.Status == OutboxEntryStatus.Pending ||
                    (s.Entry.Status == OutboxEntryStatus.Processing && s.ClaimedUntil <= now))
                .OrderBy(s => s.Entry.CreatedAt)
                .Take(limit);

            foreach (var state in eligible)
            {
                var token = Guid.NewGuid();
                var expiresAt = now + claimLease;

                // Update the entry's status in the stored record.
                state.Entry = state.Entry with { Status = OutboxEntryStatus.Processing };
                state.ClaimToken = token;
                state.ClaimedUntil = expiresAt;

                results.Add(new OutboxClaim(state.Entry, token, expiresAt));
            }
        }

        return Task.FromResult<IReadOnlyList<OutboxClaim>>(results);
    }

    /// <inheritdoc/>
    public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            if (!_entries.TryGetValue(claim.Entry.Id, out var state))
                return Task.FromResult(false);

            // Reject stale or expired claims — mirrors the PostgreSQL WHERE clause:
            //   status = 'processing' AND claim_id = @claim_id AND claim_expires_at > now()
            if (state.ClaimToken != claim.Token || state.ClaimedUntil <= now)
                return Task.FromResult(false);

            state.Entry = state.Entry with
            {
                Status = OutboxEntryStatus.Delivered,
                DeliveredAt = now
            };
            state.ClaimToken = Guid.Empty;
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(error);
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            if (!_entries.TryGetValue(claim.Entry.Id, out var state))
                return Task.FromResult(false);

            // Same fencing as MarkDeliveredAsync.
            if (state.ClaimToken != claim.Token || state.ClaimedUntil <= now)
                return Task.FromResult(false);

            state.Entry = state.Entry with
            {
                Status = OutboxEntryStatus.Failed,
                RetryCount = state.Entry.RetryCount + 1,
                LastError = error
            };
            state.ClaimToken = Guid.Empty;
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only entries whose status is <see cref="OutboxEntryStatus.Failed"/> are eligible.
    /// Entries that are <c>processing</c> with an expired lease are not reset here —
    /// <see cref="ClaimPendingAsync"/> handles those by re-claiming them.
    /// </remarks>
    public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var state in _entries.Values)
            {
                if (state.Entry.Status != OutboxEntryStatus.Failed)
                    continue;
                if (messageType is not null && state.Entry.MessageType != messageType)
                    continue;

                state.Entry = state.Entry with
                {
                    Status = OutboxEntryStatus.Pending,
                    RetryCount = 0,
                    LastError = null
                };
                state.ClaimToken = Guid.Empty;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var toRemove = _entries.Values
                .Where(s => s.Entry.Status == OutboxEntryStatus.Delivered &&
                            s.Entry.DeliveredAt < before)
                .Select(s => s.Entry.Id)
                .ToList();

            foreach (var id in toRemove)
            {
                if (_entries.TryGetValue(id, out var state))
                {
                    _entries.Remove(id);
                    _sourceEventIds.Remove(state.Entry.SourceEventId);
                }
            }
        }

        return Task.CompletedTask;
    }
}
