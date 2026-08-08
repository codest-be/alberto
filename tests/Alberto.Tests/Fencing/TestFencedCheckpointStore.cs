using Alberto.Subscriptions;

namespace Alberto.Tests.Fencing;

/// <summary>
/// Test-only in-memory implementation of <see cref="IFencedCheckpointStore"/> backed by a
/// <see cref="TestProcessorLeaseManager"/>. Mirrors the production PostgreSQL two-layer
/// fencing model in process so that the <see cref="Alberto.Testing.Xunit.FencedCheckpointStoreSpecification"/>
/// can run without a database.
/// </summary>
/// <remarks>
/// <para>
/// The fencing model matches the PostgreSQL implementation in two ordered layers:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Lease guard (guard 1)</b>: the write is rejected unless the lease table shows the correct
///     replica, the correct fence token, and a non-expired expiry.
///   </description></item>
///   <item><description>
///     <b>Checkpoint-row guard (guard 2)</b>: even when the lease check passes, the write is
///     rejected if the checkpoint row already carries a <em>higher</em> fence token. This is
///     defence in depth: in normal operation the two guards are co-satisfied by the same
///     monotonic token sequence, so guard 2 cannot fire through the public API — the
///     monotonicity of the token sequence guarantees that any token that passes guard 1 is
///     already >= the stored checkpoint token. <see cref="InjectCheckpointFenceToken"/> exists
///     precisely to exercise this unreachable path in isolation.
///   </description></item>
/// </list>
/// <para>
/// <see cref="SaveAsync"/> (the unfenced path) applies GREATEST semantics and does not
/// update the stored fence token, matching the PostgreSQL implementation.
/// </para>
/// </remarks>
internal sealed class TestFencedCheckpointStore : IFencedCheckpointStore, ICheckpointInventory
{
    private readonly TestProcessorLeaseManager _leaseManager;
    private readonly object _lock = new();
    private readonly Dictionary<string, CheckpointRecord> _checkpoints = new();

    private readonly record struct CheckpointRecord(long Position, long FenceToken);

    internal TestFencedCheckpointStore(TestProcessorLeaseManager leaseManager)
    {
        _leaseManager = leaseManager;
    }

    // -------------------------------------------------------------------------
    // Test seeding — allows guard 2 to be exercised independently of guard 1.
    // The two cannot be pried apart through the public API because the same
    // monotonic token sequence co-satisfies both: any token that passes guard 1
    // is already >= the stored checkpoint token.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Seeds the checkpoint row for <paramref name="processorId"/> with the supplied
    /// <paramref name="position"/> and <paramref name="fenceToken"/> without going
    /// through any guard, enabling guard 2 to be exercised in isolation.
    /// </summary>
    internal void InjectCheckpointFenceToken(string processorId, long position, long fenceToken)
    {
        lock (_lock)
            _checkpoints[processorId] = new CheckpointRecord(position, fenceToken);
    }

    // -------------------------------------------------------------------------
    // ICheckpointStore
    // -------------------------------------------------------------------------

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

    /// <remarks>
    /// Applies GREATEST semantics: a position lower than the stored value is silently
    /// discarded. The stored fence token is not changed by this unfenced path.
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
                _checkpoints[processorId] = new CheckpointRecord(position, FenceToken: 0);
            }

            return Task.CompletedTask;
        }
    }

    public Task ResetAsync(string processorId, CancellationToken ct = default)
    {
        lock (_lock)
            _checkpoints.Remove(processorId);

        return Task.CompletedTask;
    }

    /// <remarks>
    /// Overwrites the position unconditionally — backward moves are permitted.
    /// The existing fence token is preserved.
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

    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="useProcessorLeaseFencing"/> is <see langword="false"/>.
    /// Tenant-lease fencing has no in-memory tenant-lease infrastructure to check against.
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
                $"{nameof(TestFencedCheckpointStore)} does not support tenant-lease fencing " +
                $"(useProcessorLeaseFencing = false). Pass true, or use the PostgreSQL implementation.");

        lock (_lock)
        {
            // Guard 1 — lease check
            var lease = _leaseManager.GetActiveLease(consumerId, processorId);
            if (lease is null || lease.Value.ReplicaId != replicaId || lease.Value.FenceToken != fenceToken)
                return Task.FromResult(false);

            // Guard 2 — checkpoint-row check (defence in depth; unreachable in normal operation,
            // exercised via InjectCheckpointFenceToken)
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

    public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<string>>(_checkpoints.Keys.ToList());
    }
}
