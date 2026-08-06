using Alberto.Subscriptions;

namespace Alberto.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IProcessorLeaseManager"/>.
/// Stores leases in a dictionary backed by a <see cref="TimeProvider"/> for
/// controllable expiry in tests. Uses an atomically incrementing counter for
/// fence tokens, mirroring the strictly-increasing guarantee of the PostgreSQL
/// sequence in <c>alberto_fence_tokens</c>.
/// </summary>
/// <remarks>
/// <para>
/// State is process-local and not shared between instances. All methods are
/// thread-safe via a single lock. Intended for unit tests and local development;
/// for production multi-replica scenarios, use the PostgreSQL implementation.
/// </para>
/// <para>
/// Every call to <see cref="TryAcquireAsync"/> that succeeds — including a same-replica
/// re-acquisition — issues a new fence token from the monotonic counter. Only
/// <see cref="RenewLeasesAsync"/> extends an existing lease without changing its token.
/// </para>
/// </remarks>
public sealed class InMemoryProcessorLeaseManager : IProcessorLeaseManager
{
    private readonly TimeProvider _clock;
    private long _tokenSequence;
    private readonly Dictionary<(string ConsumerId, string ProcessorId), LeaseRecord> _leases = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Initialises a new lease manager.
    /// </summary>
    /// <param name="clock">
    /// Clock used for lease expiry evaluation. Pass a <c>FakeTimeProvider</c>
    /// (from <c>Microsoft.Extensions.TimeProvider.Testing</c>) to control time in tests.
    /// Defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    /// <param name="leaseDuration">
    /// How long a lease remains valid after acquisition or renewal.
    /// Defaults to 30 seconds.
    /// </param>
    public InMemoryProcessorLeaseManager(
        TimeProvider? clock = null,
        TimeSpan? leaseDuration = null)
    {
        _clock = clock ?? TimeProvider.System;
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
    }

    internal readonly record struct LeaseRecord(
        string ReplicaId,
        DateTimeOffset AcquiredAt,
        DateTimeOffset ExpiresAt,
        long FenceToken);

    /// <summary>
    /// Returns the active lease record for a processor, or <see langword="null"/> if the
    /// lease does not exist or has expired. Called by <see cref="InMemoryFencedCheckpointStore"/>
    /// to check the lease before writing a checkpoint.
    /// </summary>
    internal LeaseRecord? GetActiveLease(string consumerId, string processorId)
    {
        lock (_lock)
        {
            var key = (consumerId, processorId);
            if (_leases.TryGetValue(key, out var record) && record.ExpiresAt > _clock.GetUtcNow())
                return record;

            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Acquires the lease if no live lease exists for the processor, or if the caller is the
    /// same replica that already holds it. Returns <see langword="null"/> when another replica
    /// holds a live lease.
    /// <para>
    /// Every successful acquisition issues a new fence token even when the same replica
    /// re-acquires, mirroring the PostgreSQL behaviour where every INSERT path draws
    /// <c>nextval('alberto_fence_tokens')</c>. The token is <em>not</em> updated by
    /// <see cref="RenewLeasesAsync"/>; a renewal preserves the token of the acquisition it extends.
    /// </para>
    /// </remarks>
    public Task<IProcessorLease?> TryAcquireAsync(
        string consumerId, string processorId, string replicaId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var key = (consumerId, processorId);
            var newExpiry = now + LeaseDuration;

            if (_leases.TryGetValue(key, out var existing))
            {
                var isExpired = existing.ExpiresAt <= now;
                var isSameReplica = existing.ReplicaId == replicaId;

                if (!isExpired && !isSameReplica)
                    return Task.FromResult<IProcessorLease?>(null);

                // Same replica re-acquires: preserve the original AcquiredAt so the holding
                // period is measured from the first acquisition, not the re-acquisition.
                var acquiredAt = isSameReplica && !isExpired ? existing.AcquiredAt : now;
                var newToken = Interlocked.Increment(ref _tokenSequence);
                _leases[key] = new LeaseRecord(replicaId, acquiredAt, newExpiry, newToken);
                return Task.FromResult<IProcessorLease?>(new ProcessorLease(processorId, newExpiry, newToken));
            }
            else
            {
                var newToken = Interlocked.Increment(ref _tokenSequence);
                _leases[key] = new LeaseRecord(replicaId, now, newExpiry, newToken);
                return Task.FromResult<IProcessorLease?>(new ProcessorLease(processorId, newExpiry, newToken));
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Extends the expiry of every live lease this replica holds within the consumer group.
    /// The fence token is intentionally unchanged — renewal is a continuation of the same
    /// ownership stretch, not a new acquisition.
    /// </remarks>
    public Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var newExpiry = now + LeaseDuration;
            var renewed = new List<string>();

            foreach (var key in _leases.Keys.ToList())
            {
                if (key.ConsumerId != consumerId)
                    continue;

                var record = _leases[key];
                if (record.ReplicaId != replicaId || record.ExpiresAt <= now)
                    continue;

                // Renew: extend expiry, preserve fence token
                _leases[key] = record with { ExpiresAt = newExpiry };
                renewed.Add(key.ProcessorId);
            }

            return Task.FromResult<IReadOnlyList<string>>(renewed);
        }
    }

    /// <inheritdoc/>
    public Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var toRemove = _leases.Keys
                .Where(k => k.ConsumerId == consumerId && _leases[k].ReplicaId == replicaId)
                .ToList();

            foreach (var key in toRemove)
                _leases.Remove(key);

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns all lease rows for the consumer, including expired ones, matching the PostgreSQL
    /// implementation which returns every row in <c>alberto_processor_leases</c> without
    /// filtering on <c>expires_at</c>. Used for monitoring.
    /// </remarks>
    public Task<IReadOnlyList<ProcessorLeaseInfo>> GetAllLeasesAsync(
        string consumerId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ProcessorLeaseInfo> result = _leases
                .Where(kv => kv.Key.ConsumerId == consumerId)
                .Select(kv => new ProcessorLeaseInfo(
                    kv.Key.ProcessorId,
                    kv.Value.ReplicaId,
                    kv.Value.AcquiredAt,
                    kv.Value.ExpiresAt))
                .ToArray();

            return Task.FromResult(result);
        }
    }
}
