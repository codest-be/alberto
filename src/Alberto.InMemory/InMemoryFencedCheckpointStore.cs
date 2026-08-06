using Alberto.Subscriptions;

namespace Alberto.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IFencedCheckpointStore"/> backed by an
/// <see cref="InMemoryProcessorLeaseManager"/>. Suitable for unit tests and local
/// development; for production multi-replica scenarios, use the PostgreSQL implementation.
/// </summary>
/// <remarks>
/// <para>
/// The fencing model matches the PostgreSQL implementation in two ordered layers:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Lease guard</b>: the write is rejected unless the lease table shows the correct
///     replica, the correct fence token, and a non-expired expiry. This is the primary guard:
///     it rejects writes from replicas that have lost the lease entirely, or from the same
///     replica presenting a stale fence token from a previous ownership stretch.
///   </description></item>
///   <item><description>
///     <b>Checkpoint-row guard</b>: even when the lease check passes, the write is rejected
///     if the checkpoint row already carries a <em>higher</em> fence token. This is defence
///     in depth: it guards against a superseded generation that somehow bypassed the lease
///     check (e.g. via an in-process cache that has not yet observed the lease transition).
///     In normal operation the two guards are co-satisfied by the same monotonic token
///     sequence and the row guard cannot fire independently of the lease guard.
///   </description></item>
/// </list>
/// <para>
/// <see cref="SaveAsync"/> (the unfenced path) applies GREATEST semantics and does not
/// update the stored fence token, exactly as in the PostgreSQL implementation.
/// </para>
/// <para>
/// Tenant-lease fencing (<c>useProcessorLeaseFencing = false</c>) is not supported;
/// calling <see cref="SaveIfLeaseHeldAsync"/> with that flag throws
/// <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class InMemoryFencedCheckpointStore : IFencedCheckpointStore, ICheckpointInventory
{
    private readonly InMemoryProcessorLeaseManager _leaseManager;
    private readonly object _lock = new();
    private readonly Dictionary<string, CheckpointRecord> _checkpoints = new();

    private readonly record struct CheckpointRecord(long Position, long FenceToken);

    /// <summary>
    /// Initialises the store paired with the supplied lease manager.
    /// The lease manager and this store must use the same backing state
    /// (pass the same <see cref="InMemoryProcessorLeaseManager"/> instance).
    /// </summary>
    /// <param name="leaseManager">
    /// The lease manager used to verify that a write comes from the current lease holder.
    /// </param>
    public InMemoryFencedCheckpointStore(InMemoryProcessorLeaseManager leaseManager)
    {
        _leaseManager = leaseManager;
    }

    // -------------------------------------------------------------------------
    // Internal seeding — visible to Alberto.Tests via InternalsVisibleTo.
    // Allows tests to inject a checkpoint row with an arbitrary fence token so
    // that the checkpoint-row guard (guard 2) can be exercised independently of
    // the lease guard (guard 1). The two cannot be pried apart through the
    // public API because the same monotonic token sequence co-satisfies both.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Seeds the checkpoint row for <paramref name="processorId"/> with the supplied
    /// <paramref name="position"/> and <paramref name="fenceToken"/> without going
    /// through any guard. Test-only; exposed via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal void InjectCheckpointFenceToken(string processorId, long position, long fenceToken)
    {
        lock (_lock)
            _checkpoints[processorId] = new CheckpointRecord(position, fenceToken);
    }

    // -------------------------------------------------------------------------
    // ICheckpointStore
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            long? result = _checkpoints.TryGetValue(processorId, out var record)
                ? record.Position
                : null;

            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Applies GREATEST semantics: a position lower than the current stored position is
    /// silently discarded. The stored fence token is not changed by this unfenced path,
    /// mirroring the PostgreSQL <c>ON CONFLICT DO UPDATE SET position = GREATEST(...)</c>
    /// without touching <c>fence_token</c>.
    /// </remarks>
    public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_checkpoints.TryGetValue(processorId, out var existing))
            {
                if (position > existing.Position)
                    _checkpoints[processorId] = existing with { Position = position };
            }
            else
            {
                // First write via the unfenced path: fence token 0 signals no fenced write
                // has occurred yet for this processor.
                _checkpoints[processorId] = new CheckpointRecord(position, FenceToken: 0);
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task ResetAsync(string processorId, CancellationToken ct = default)
    {
        lock (_lock)
            _checkpoints.Remove(processorId);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Overwrites the position unconditionally — backward moves are permitted.
    /// The existing fence token is preserved, matching the PostgreSQL operator path
    /// which touches only the position column.
    /// </remarks>
    public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var fenceToken = _checkpoints.TryGetValue(processorId, out var existing)
                ? existing.FenceToken
                : 0L;

            _checkpoints[processorId] = new CheckpointRecord(position, fenceToken);
        }

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // IFencedCheckpointStore
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="useProcessorLeaseFencing"/> is <see langword="false"/>.
    /// Tenant-lease fencing has no in-memory tenant-lease infrastructure to check against.
    /// Pass <see langword="true"/> or use the PostgreSQL implementation.
    /// </exception>
    public Task<bool> SaveIfLeaseHeldAsync(
        string processorId,
        long position,
        string consumerId,
        string replicaId,
        long fenceToken,
        bool useProcessorLeaseFencing = false,
        CancellationToken ct = default)
    {
        if (!useProcessorLeaseFencing)
            throw new NotSupportedException(
                $"{nameof(InMemoryFencedCheckpointStore)} does not support tenant-lease fencing " +
                $"(useProcessorLeaseFencing = false). Pass true, or use the PostgreSQL implementation.");

        lock (_lock)
        {
            // Guard 1 — lease check: the calling replica must hold an active lease for this
            // processor, and must present the exact fence token of that lease. This rejects:
            //   - replicas that have never held the lease
            //   - replicas whose lease has expired
            //   - replicas presenting a stale token from a previous ownership stretch
            var lease = _leaseManager.GetActiveLease(consumerId, processorId);
            if (lease is null || lease.Value.ReplicaId != replicaId || lease.Value.FenceToken != fenceToken)
                return Task.FromResult(false);

            // Guard 2 — checkpoint-row check: defence in depth against a superseded generation
            // that somehow bypassed the lease guard. If the checkpoint row already carries a
            // higher fence token, a write with a lower token is silently rejected.
            _checkpoints.TryGetValue(processorId, out var existing);
            if (existing.FenceToken > fenceToken)
                return Task.FromResult(false);

            // Both guards passed: apply GREATEST to the position and record the fence token.
            var newPosition = existing.Position > position ? existing.Position : position;
            _checkpoints[processorId] = new CheckpointRecord(newPosition, fenceToken);
            return Task.FromResult(true);
        }
    }

    // -------------------------------------------------------------------------
    // ICheckpointInventory
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<string>>(_checkpoints.Keys.ToList());
    }
}
