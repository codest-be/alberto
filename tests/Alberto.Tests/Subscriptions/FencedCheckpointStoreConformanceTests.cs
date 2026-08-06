using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Runs the <see cref="FencedCheckpointStoreSpecification"/> against the in-memory adapter
/// pair: <see cref="InMemoryProcessorLeaseManager"/> + <see cref="InMemoryFencedCheckpointStore"/>.
///
/// No Docker, no database, no integration trait — all fencing behaviour is exercised in process
/// with time advanced by a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class InMemoryFencedCheckpointStoreConformanceTests : FencedCheckpointStoreSpecification
{
    private readonly FakeTimeProvider _clock = new();
    private readonly InMemoryProcessorLeaseManager _leaseManager;
    private readonly InMemoryFencedCheckpointStore _store;

    public InMemoryFencedCheckpointStoreConformanceTests()
    {
        _leaseManager = new InMemoryProcessorLeaseManager(_clock);
        _store = new InMemoryFencedCheckpointStore(_leaseManager);
    }

    protected override Task<IProcessorLeaseManager> CreateLeaseManager() =>
        Task.FromResult<IProcessorLeaseManager>(_leaseManager);

    protected override Task<IFencedCheckpointStore> CreateStore() =>
        Task.FromResult<IFencedCheckpointStore>(_store);

    /// <summary>
    /// Advances the fake clock well past the lease duration so that the lease for
    /// <paramref name="processorId"/> reads as expired on the next check, without any
    /// real-time wait.
    /// </summary>
    protected override Task ExpireLeaseAsync(string processorId)
    {
        _clock.Advance(TimeSpan.FromHours(1));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Implementation-specific tests for <see cref="InMemoryFencedCheckpointStore"/> that
/// exercise code paths unreachable through the public API.
///
/// The checkpoint-row fence-token guard (guard 2) cannot fire independently of the lease
/// guard (guard 1) through the public API, because the same monotonic token sequence
/// co-satisfies both: the lease and the checkpoint always carry the same token.
/// <c>InjectCheckpointFenceToken</c> (internal, exposed via <c>InternalsVisibleTo</c>)
/// seeds a checkpoint row with an arbitrary fence token so the row guard can be tested
/// in isolation.
/// </summary>
public sealed class InMemoryFencedCheckpointStoreGuard2Tests
{
    /// <summary>
    /// When the checkpoint row carries a fence token higher than the one presented,
    /// the write is rejected even though the lease check would pass.
    ///
    /// Scenario: generation T_high wrote to the checkpoint row (fenceToken = T_high),
    /// and then the lease was re-acquired by the same replica with token T_low (impossible
    /// in practice, but directly injected here to test the guard in isolation).
    /// The write with T_low must be rejected to prevent the checkpoint from rolling back.
    /// </summary>
    [Fact]
    public async Task CheckpointRowHasHigherToken_FencedWrite_ReturnsFalseAndDoesNotWrite()
    {
        var clock = new FakeTimeProvider();
        var leaseManager = new InMemoryProcessorLeaseManager(clock);
        var store = new InMemoryFencedCheckpointStore(leaseManager);

        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        // Acquire lease — token T (will be 1, 2, or similar small value)
        var lease = await leaseManager.TryAcquireAsync(
            consumerId, processorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        var leaseToken = lease!.FenceToken;

        // Inject a checkpoint row with a fence token much higher than the lease token.
        // This simulates the state left by a later generation that has already written.
        var highToken = leaseToken + 1000;
        store.InjectCheckpointFenceToken(processorId, position: 500, fenceToken: highToken);

        // Guard 1 (lease check) passes: same replica, correct token, not expired.
        // Guard 2 (checkpoint-row check) must fire: highToken > leaseToken.
        var saved = await store.SaveIfLeaseHeldAsync(
            processorId, position: 600,
            consumerId: consumerId, replicaId: replicaId, fenceToken: leaseToken,
            useProcessorLeaseFencing: true,
            ct: TestContext.Current.CancellationToken);

        Assert.False(saved);

        // Checkpoint must not have moved forward from the injected value
        Assert.Equal(500, await store.GetAsync(processorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// When the checkpoint row carries the same fence token as the one presented,
    /// the write proceeds (GREATEST applied to position). Equal tokens are a repeat write
    /// from the same generation, which is valid (idempotent forward progress).
    /// </summary>
    [Fact]
    public async Task CheckpointRowHasSameToken_FencedWrite_Succeeds()
    {
        var clock = new FakeTimeProvider();
        var leaseManager = new InMemoryProcessorLeaseManager(clock);
        var store = new InMemoryFencedCheckpointStore(leaseManager);

        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leaseManager.TryAcquireAsync(
            consumerId, processorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        // First write — establishes position 100 with the lease token
        var first = await store.SaveIfLeaseHeldAsync(
            processorId, position: 100,
            consumerId: consumerId, replicaId: replicaId, fenceToken: lease!.FenceToken,
            useProcessorLeaseFencing: true,
            ct: TestContext.Current.CancellationToken);
        Assert.True(first);

        // Second write with the same token and a higher position — must succeed
        var second = await store.SaveIfLeaseHeldAsync(
            processorId, position: 200,
            consumerId: consumerId, replicaId: replicaId, fenceToken: lease!.FenceToken,
            useProcessorLeaseFencing: true,
            ct: TestContext.Current.CancellationToken);
        Assert.True(second);

        Assert.Equal(200, await store.GetAsync(processorId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Calling <see cref="InMemoryFencedCheckpointStore.SaveIfLeaseHeldAsync"/> with
    /// <c>useProcessorLeaseFencing = false</c> throws <see cref="NotSupportedException"/>.
    /// Tenant-lease fencing has no in-memory infrastructure to check against.
    /// </summary>
    [Fact]
    public async Task TenantLeaseFencing_ThrowsNotSupportedException()
    {
        var leaseManager = new InMemoryProcessorLeaseManager();
        var store = new InMemoryFencedCheckpointStore(leaseManager);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.SaveIfLeaseHeldAsync(
                processorId: "proc", position: 1,
                consumerId: "consumer", replicaId: "replica", fenceToken: 1,
                useProcessorLeaseFencing: false));
    }
}
