using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Alberto.InMemory;
using FluentAssertions;
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

    // A stored fence token of 7 outranks the lease's token 1, so the fenced write is refused
    // only if the unfenced operation left the token alone.
    [Theory]
    [InlineData("save")]
    [InlineData("rewind")]
    public async Task Unfenced_writes_preserve_the_stored_fence_token(string operation)
    {
        var leaseManager = new InMemoryProcessorLeaseManager();
        var store = new InMemoryFencedCheckpointStore(leaseManager);
        store.InjectCheckpointFenceToken("proc", position: 10, fenceToken: 7);
        var lease = await leaseManager.TryAcquireAsync("consumer", "proc", "replica");

        if (operation == "save") await store.SaveAsync("proc", 20);
        else await store.RewindAsync("proc", 3);

        (await store.SaveIfLeaseHeldAsync("proc", 30, "consumer", "replica", lease!.FenceToken, useProcessorLeaseFencing: true))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ListProcessorIdsAsync_returns_every_checkpointed_processor()
    {
        var store = new InMemoryFencedCheckpointStore(new InMemoryProcessorLeaseManager());
        await store.SaveAsync("a", 1);
        await store.RewindAsync("b", 2);

        (await store.ListProcessorIdsAsync()).Should().BeEquivalentTo(["a", "b"]);
    }
}

/// <summary>
/// The in-memory fenced store is the module's <see cref="ICheckpointStore"/> now, so it has to meet
/// the plain checkpoint contract too: GREATEST on save, rewind, reset, inventory.
/// </summary>
public sealed class InMemoryFencedCheckpointStoreCheckpointTests : CheckpointStoreSpecification
{
    protected override Task<ICheckpointStore> CreateStore() =>
        Task.FromResult<ICheckpointStore>(new InMemoryFencedCheckpointStore(new InMemoryProcessorLeaseManager()));
}
