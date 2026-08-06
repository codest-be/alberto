using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Testing.Xunit;

/// <summary>
/// Conformance specification for the fencing seam: the combination of
/// <see cref="IProcessorLeaseManager"/> and <see cref="IFencedCheckpointStore"/>.
///
/// Derive from this class, implement the factory/helper members, and every
/// Alberto-defined fencing contract fact runs against your adapter pair.
/// </summary>
/// <remarks>
/// <para>
/// The fencing seam has two components that must work together: a lease manager that
/// issues strictly-increasing fence tokens per acquisition, and a fenced checkpoint store
/// that rejects writes unless the caller presents the current live lease token.
/// Testing them in isolation misses the contract — the spec therefore covers both.
/// </para>
/// <para>
/// Each xUnit test-class instance is created fresh per <c>[Fact]</c> method, so
/// <see cref="CreateLeaseManager"/> and <see cref="CreateStore"/> are called once per fact.
/// Both must return instances that share the same backing store (e.g. the same in-memory
/// dictionary or the same PostgreSQL data source).
/// </para>
/// <para>
/// Facts are limited to <c>useProcessorLeaseFencing = true</c> (the processor-lease path
/// used by the control loop). Tenant-lease fencing has no conformance contract here.
/// </para>
/// <para>
/// <b>Guard 2 (checkpoint-row fence-token check)</b>: even when the lease check (guard 1)
/// passes, a conforming fenced checkpoint store must also reject a write when the checkpoint
/// row already carries a <em>higher</em> fence token — defence in depth against a superseded
/// generation. Guard 2 cannot be provoked through <see cref="IFencedCheckpointStore"/> alone
/// because the token sequence is strictly increasing: for guard 1 to pass, the presented token
/// must equal the live lease's current token, and the current lease token is always ≥ the
/// stored checkpoint token (the row is only ever written by a token that itself passed guard 1
/// at that moment). <see cref="SeedCheckpointFenceTokenAsync"/> bypasses that impossibility
/// by writing the checkpoint row at an arbitrarily high token so that guard 2 can be specified
/// and mutation-killed. Subclasses provide their adapter-specific seeding mechanism (direct
/// method call for in-memory, a raw SQL INSERT/UPDATE for PostgreSQL).
/// </para>
/// </remarks>
public abstract class FencedCheckpointStoreSpecification
{
    /// <summary>
    /// Unique consumer-group identity generated per test instance for isolation.
    /// </summary>
    protected string ConsumerId { get; } = $"consumer-{Guid.NewGuid():N}";

    /// <summary>
    /// Unique processor identity generated per test instance for isolation.
    /// </summary>
    protected string ProcessorId { get; } = $"processor-{Guid.NewGuid():N}";

    /// <summary>
    /// Creates the lease manager under test. Must share backing state with the store
    /// returned by <see cref="CreateStore"/> so that a lease acquired via this manager
    /// is visible to a fenced write on that store.
    /// </summary>
    protected abstract Task<IProcessorLeaseManager> CreateLeaseManager();

    /// <summary>
    /// Creates the fenced checkpoint store under test. Must share backing state with
    /// the lease manager returned by <see cref="CreateLeaseManager"/>.
    /// </summary>
    protected abstract Task<IFencedCheckpointStore> CreateStore();

    /// <summary>
    /// Forces the lease held by any replica for
    /// <paramref name="processorId"/> within <see cref="ConsumerId"/> to appear
    /// expired, without waiting for wall-clock time to pass.
    /// </summary>
    /// <remarks>
    /// In-memory implementations advance a <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>
    /// well past the lease duration. PostgreSQL implementations UPDATE the row directly.
    /// </remarks>
    /// <param name="processorId">The processor whose lease should be expired.</param>
    protected abstract Task ExpireLeaseAsync(string processorId);

    /// <summary>
    /// Seeds the checkpoint row for <paramref name="processorId"/> with the supplied
    /// <paramref name="position"/> and <paramref name="fenceToken"/> without going through
    /// any guard, enabling guard 2 (checkpoint-row fence-token check) to be exercised in
    /// isolation.
    /// </summary>
    /// <remarks>
    /// In-memory implementations call directly into the test adapter. PostgreSQL
    /// implementations issue a raw INSERT … ON CONFLICT DO UPDATE on
    /// <c>alberto_processor_checkpoints</c>.
    /// </remarks>
    /// <param name="processorId">Processor whose checkpoint row should be seeded.</param>
    /// <param name="position">Checkpoint position to write.</param>
    /// <param name="fenceToken">Fence token to write — typically higher than any lease token.</param>
    protected abstract Task SeedCheckpointFenceTokenAsync(
        string processorId, long position, long fenceToken);

    // -------------------------------------------------------------------------
    // Helper: fenced write with processor-lease fencing enabled.
    // All spec facts use useProcessorLeaseFencing: true.
    // -------------------------------------------------------------------------

    private static Task<bool> FencedSave(
        IFencedCheckpointStore store,
        string processorId,
        long position,
        string consumerId,
        string replicaId,
        long fenceToken,
        CancellationToken ct = default) =>
        store.SaveIfLeaseHeldAsync(
            processorId, position, consumerId, replicaId, fenceToken,
            useProcessorLeaseFencing: true, ct);

    // =========================================================================
    // Processor-lease fencing facts
    // =========================================================================

    /// <summary>
    /// A fenced write by the replica that holds the current lease returns
    /// <see langword="true"/> and the position is visible via <c>GetAsync</c>.
    /// This is the happy path: single replica, no contention.
    /// </summary>
    [Fact]
    public async Task HeldLease_FencedWrite_ReturnsTrueAndPersistsPosition()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaId, TestContext.Current.CancellationToken);

        Assert.NotNull(lease);

        var saved = await FencedSave(store, ProcessorId, 42, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);

        Assert.True(saved);
        Assert.Equal(42, await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A fenced write presenting a different <c>replicaId</c> than the one that holds the
    /// lease returns <see langword="false"/> and does not modify the checkpoint.
    ///
    /// Defends against: a replica that never held the lease attempting to write.
    /// </summary>
    [Fact]
    public async Task WrongReplica_FencedWrite_ReturnsFalseAndDoesNotWrite()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var ownerReplica = $"owner-{Guid.NewGuid():N}";
        var intruderReplica = $"intruder-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, ownerReplica, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        // Intruder uses the correct fenceToken but wrong replicaId
        var saved = await FencedSave(store, ProcessorId, 99, ConsumerId, intruderReplica, lease!.FenceToken,
            TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.Null(await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A fenced write after the lease has expired returns <see langword="false"/> and does
    /// not modify the checkpoint.
    ///
    /// Defends against: a zombie consumer that continues processing after losing the lease.
    /// </summary>
    [Fact]
    public async Task ExpiredLease_FencedWrite_ReturnsFalseAndDoesNotWrite()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        await ExpireLeaseAsync(ProcessorId);

        var saved = await FencedSave(store, ProcessorId, 99, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.Null(await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A fenced write presenting the fence token of a <em>previous</em> ownership stretch
    /// is rejected even when the same replica has since re-acquired an active lease.
    ///
    /// This is the core generational safety guarantee: re-acquiring the lease does not
    /// resurrect buffered writes prepared under an earlier token. Without it, a replica that
    /// loses the lease and re-takes it could advance the checkpoint past everything the
    /// intervening owner wrote.
    /// </summary>
    [Fact]
    public async Task SupersededToken_FencedWrite_ReturnsFalseEvenWithActiveLease()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaA = $"replicaA-{Guid.NewGuid():N}";
        var replicaB = $"replicaB-{Guid.NewGuid():N}";

        // replicaA acquires T1, writes checkpoint 50
        var leaseT1 = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaA, TestContext.Current.CancellationToken);
        Assert.NotNull(leaseT1);
        await FencedSave(store, ProcessorId, 50, ConsumerId, replicaA, leaseT1!.FenceToken,
            TestContext.Current.CancellationToken);

        // replicaB takes over, advances checkpoint to 200
        await ExpireLeaseAsync(ProcessorId);
        var leaseT2 = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaB, TestContext.Current.CancellationToken);
        Assert.NotNull(leaseT2);
        await FencedSave(store, ProcessorId, 200, ConsumerId, replicaB, leaseT2!.FenceToken,
            TestContext.Current.CancellationToken);

        // replicaA re-acquires the lease (gets T3, a new generation)
        await ExpireLeaseAsync(ProcessorId);
        var leaseT3 = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaA, TestContext.Current.CancellationToken);
        Assert.NotNull(leaseT3);

        // A buffered write prepared under T1 must be rejected, even though replicaA
        // now holds an active lease (T3). T1 is a superseded generation.
        var saved = await FencedSave(store, ProcessorId, 500, ConsumerId, replicaA, leaseT1.FenceToken,
            TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.Equal(200, await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Renewing a lease via <see cref="IProcessorLeaseManager.RenewLeasesAsync"/> does not
    /// change the fence token — a write using the original token still succeeds after renewal.
    ///
    /// Renewal is a <em>continuation</em> of the same ownership stretch, not a new acquisition.
    /// The control loop renews the lease periodically, and the token must remain stable across
    /// those renewals so that in-flight fenced writes continue to be accepted.
    /// </summary>
    [Fact]
    public async Task LeaseRenewal_PreservesToken_FencedWriteSucceeds()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        var originalToken = lease!.FenceToken;

        // Renew — must not change the token
        await leases.RenewLeasesAsync(ConsumerId, replicaId, TestContext.Current.CancellationToken);

        var saved = await FencedSave(store, ProcessorId, 77, ConsumerId, replicaId, originalToken,
            TestContext.Current.CancellationToken);

        Assert.True(saved);
        Assert.Equal(77, await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Fenced writes by the same lease holder apply GREATEST semantics to the position:
    /// a backward position is silently discarded and the stored position does not decrease.
    ///
    /// Matches the Postgres <c>GREATEST</c> in the upsert, which prevents a stale buffer
    /// flush from undoing progress already written by the same holder.
    /// </summary>
    [Fact]
    public async Task FencedWrites_ApplyGreatestSemantics_BackwardPositionDiscarded()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        // Forward write succeeds
        var first = await FencedSave(store, ProcessorId, 100, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);
        Assert.True(first);

        // Backward write returns true (store accepted the call) but position stays at 100
        var second = await FencedSave(store, ProcessorId, 50, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);
        Assert.True(second);

        Assert.Equal(100, await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Fence tokens are strictly increasing across successful acquisitions.
    /// Each acquisition must produce a token greater than all preceding tokens, regardless of
    /// which replica acquires or how many times the lease has changed hands.
    ///
    /// Monotonicity is the foundation of the generational safety guarantee: a higher token
    /// always means "acquired later" and therefore authorised to write after all lower tokens.
    /// </summary>
    [Fact]
    public async Task FenceTokens_StrictlyIncreasing_AcrossAcquisitions()
    {
        var leases = await CreateLeaseManager();
        var replicaA = $"replicaA-{Guid.NewGuid():N}";
        var replicaB = $"replicaB-{Guid.NewGuid():N}";

        var leaseT1 = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaA, TestContext.Current.CancellationToken);
        Assert.NotNull(leaseT1);

        await ExpireLeaseAsync(ProcessorId);

        var leaseT2 = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaB, TestContext.Current.CancellationToken);
        Assert.NotNull(leaseT2);

        Assert.True(leaseT2!.FenceToken > leaseT1!.FenceToken,
            $"Expected T2 ({leaseT2.FenceToken}) > T1 ({leaseT1.FenceToken})");
    }

    /// <summary>
    /// An unfenced <see cref="ICheckpointStore.SaveAsync"/> does not prevent subsequent fenced
    /// writes from being accepted. The two write paths must coexist without corrupting each other.
    ///
    /// In practice the control loop uses only fenced writes, but operator tooling may call
    /// <c>SaveAsync</c> directly. This fact pins that mixing the paths does not break fencing.
    /// </summary>
    [Fact]
    public async Task UnfencedSave_DoesNotBlockSubsequentFencedWrite()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        // Fenced write establishes a baseline
        await FencedSave(store, ProcessorId, 100, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);

        // Unfenced write attempts a backward save (discarded by GREATEST) then a forward one
        await store.SaveAsync(ProcessorId, 50, TestContext.Current.CancellationToken);
        await store.SaveAsync(ProcessorId, 150, TestContext.Current.CancellationToken);

        // Fenced write forward of the unfenced save must still succeed
        var saved = await FencedSave(store, ProcessorId, 200, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);

        Assert.True(saved);
        Assert.Equal(200, await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// While one replica holds the lease, a second replica cannot acquire it.
    /// The lease manager must enforce mutual exclusion so that at most one replica
    /// holds the right to write fenced checkpoints for a processor at any time.
    /// </summary>
    [Fact]
    public async Task ContendedLease_SecondReplica_CannotAcquire()
    {
        var leases = await CreateLeaseManager();
        var replicaA = $"replicaA-{Guid.NewGuid():N}";
        var replicaB = $"replicaB-{Guid.NewGuid():N}";

        var leaseA = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaA, TestContext.Current.CancellationToken);
        Assert.NotNull(leaseA);

        var leaseB = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaB, TestContext.Current.CancellationToken);

        Assert.Null(leaseB);
    }

    /// <summary>
    /// Two processors sharing the same <see cref="ConsumerId"/> have independent lease and
    /// checkpoint state. A fenced write on one processor must not affect the other.
    /// </summary>
    [Fact]
    public async Task MultipleProcessors_FencingIsIndependent()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var processorX = $"processorX-{Guid.NewGuid():N}";
        var processorY = $"processorY-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var leaseX = await leases.TryAcquireAsync(ConsumerId, processorX, replicaId,
            TestContext.Current.CancellationToken);
        var leaseY = await leases.TryAcquireAsync(ConsumerId, processorY, replicaId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(leaseX);
        Assert.NotNull(leaseY);

        await FencedSave(store, processorX, 111, ConsumerId, replicaId, leaseX!.FenceToken,
            TestContext.Current.CancellationToken);
        await FencedSave(store, processorY, 222, ConsumerId, replicaId, leaseY!.FenceToken,
            TestContext.Current.CancellationToken);

        Assert.Equal(111, await store.GetAsync(processorX, TestContext.Current.CancellationToken));
        Assert.Equal(222, await store.GetAsync(processorY, TestContext.Current.CancellationToken));
    }

    // =========================================================================
    // Checkpoint-row fence-token guard (guard 2) facts
    //
    // Guard 2 cannot be provoked through the interface — see class doc.
    // SeedCheckpointFenceTokenAsync bypasses the guards so these facts can run.
    // =========================================================================

    /// <summary>
    /// When the checkpoint row carries a fence token higher than the one presented,
    /// the write is rejected even though the lease check (guard 1) would pass.
    ///
    /// This is guard 2 — defence in depth. The row is seeded directly because guard 2
    /// cannot be provoked through <see cref="IFencedCheckpointStore"/>: the strictly
    /// increasing token sequence means any token that passes guard 1 is already ≥ the
    /// stored checkpoint token (see class doc for the full argument).
    /// </summary>
    [Fact]
    public async Task CheckpointRow_HigherToken_FencedWrite_ReturnsFalse()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        var leaseToken = lease!.FenceToken;

        // Seed the checkpoint row at a token much higher than the current lease token.
        // Guard 1 will pass (live lease, correct replica, correct token).
        // Guard 2 must fire (stored token > presented token).
        await SeedCheckpointFenceTokenAsync(ProcessorId, position: 500, fenceToken: leaseToken + 1000);

        var saved = await FencedSave(store, ProcessorId, 600, ConsumerId, replicaId, leaseToken,
            TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.Equal(500, await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// When the checkpoint row carries the same fence token as the one presented,
    /// the write proceeds (GREATEST applied to position). Equal tokens represent a
    /// repeat write from the same generation, which is valid (idempotent progress).
    /// </summary>
    [Fact]
    public async Task CheckpointRow_SameToken_FencedWrite_Succeeds()
    {
        var leases = await CreateLeaseManager();
        var store = await CreateStore();
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            ConsumerId, ProcessorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        // First write — establishes position 100 with the lease token
        var first = await FencedSave(store, ProcessorId, 100, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);
        Assert.True(first);

        // Second write with the same token and a higher position — must succeed
        var second = await FencedSave(store, ProcessorId, 200, ConsumerId, replicaId, lease!.FenceToken,
            TestContext.Current.CancellationToken);
        Assert.True(second);

        Assert.Equal(200, await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }
}
