using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Alberto.InMemory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Runs the <see cref="FencedCheckpointStoreSpecification"/> against the shipped in-memory adapter
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

    /// <summary>
    /// Seeds the checkpoint row directly via
    /// <see cref="InMemoryFencedCheckpointStore.InjectCheckpointFenceToken"/>.
    /// </summary>
    protected override Task SeedCheckpointFenceTokenAsync(
        string processorId, long position, long fenceToken)
    {
        _store.InjectCheckpointFenceToken(processorId, position, fenceToken);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Adapter-specific test for <see cref="InMemoryFencedCheckpointStore"/>: verifies that calling
/// <see cref="IFencedCheckpointStore.SaveIfLeaseHeldAsync"/> with
/// <c>useProcessorLeaseFencing = false</c> throws <see cref="NotSupportedException"/>.
///
/// This is not part of <see cref="FencedCheckpointStoreSpecification"/> because it is
/// in-memory-specific behaviour — the PostgreSQL adapter uses a different signal for the
/// unsupported path and has its own constraint.
/// </summary>
public sealed class InMemoryFencedCheckpointStoreAdapterTests
{
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
