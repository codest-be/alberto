using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

public class FencedCheckpointTests
{
    /// <summary>
    /// Verifies the interface hierarchy: IFencedCheckpointStore must be assignable to ICheckpointStore.
    /// This is a compile-time contract test — if it compiles, the hierarchy is correct.
    /// </summary>
    [Fact]
    public void IFencedCheckpointStore_ExtendsICheckpointStore()
    {
        IFencedCheckpointStore fenced = new InMemoryFencedCheckpointStore();

        // Must be assignable as ICheckpointStore without a cast
        ICheckpointStore store = fenced;

        Assert.NotNull(store);
    }

    /// <summary>
    /// Verifies that a store implementing IFencedCheckpointStore with SaveIfLeaseHeldAsync
    /// returning true persists the checkpoint value — i.e. the save path is exercised.
    /// </summary>
    [Fact]
    public async Task SaveIfLeaseHeldAsync_WhenLeaseHeld_SavesCheckpoint()
    {
        var store = new InMemoryFencedCheckpointStore(leaseHeld: true);

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId: "proc-1",
            position: 42,
            consumerId: "consumer-1",
            replicaId: "replica-abc",
            fenceToken: 1,
            ct: TestContext.Current.CancellationToken);

        Assert.True(saved);
        var position = await store.GetAsync("proc-1", TestContext.Current.CancellationToken);
        Assert.Equal(42, position);
    }

    /// <summary>
    /// Verifies that when the lease is not held, SaveIfLeaseHeldAsync returns false
    /// and does NOT persist the checkpoint (zombie consumer protection).
    /// </summary>
    [Fact]
    public async Task SaveIfLeaseHeldAsync_WhenLeaseExpired_ReturnsFalseAndDoesNotSave()
    {
        var store = new InMemoryFencedCheckpointStore(leaseHeld: false);

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId: "proc-1",
            position: 99,
            consumerId: "consumer-1",
            replicaId: "replica-stale",
            fenceToken: 1,
            ct: TestContext.Current.CancellationToken);

        Assert.False(saved);
        var position = await store.GetAsync("proc-1", TestContext.Current.CancellationToken);
        Assert.Null(position);
    }

    /// <summary>
    /// Verifies that a mock IFencedCheckpointStore returning true from SaveIfLeaseHeldAsync
    /// behaves like a regular save — the regular GetAsync path then reflects the stored value.
    /// </summary>
    [Fact]
    public async Task SaveIfLeaseHeldAsync_WhenNotImplemented_FallsBackToRegularSave()
    {
        // Use an implementation that always reports the lease as held,
        // and delegates the actual save to the standard SaveAsync path
        var store = new DelegatingFencedCheckpointStore();

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId: "proc-2",
            position: 77,
            consumerId: "consumer-x",
            replicaId: "replica-x",
            fenceToken: 1,
            ct: TestContext.Current.CancellationToken);

        Assert.True(saved);
        // Verify the position is readable via the base ICheckpointStore contract
        var position = await store.GetAsync("proc-2", TestContext.Current.CancellationToken);
        Assert.Equal(77, position);
        Assert.Equal(1, store.RegularSaveCount);
    }

    #region Test Helpers

    /// <summary>
    /// Simple in-memory IFencedCheckpointStore that accepts a fixed lease-held value.
    /// </summary>
    private sealed class InMemoryFencedCheckpointStore(bool leaseHeld = true) : IFencedCheckpointStore
    {
        private readonly Dictionary<string, long> _checkpoints = new();

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            _checkpoints.TryGetValue(processorId, out var value);
            long? result = _checkpoints.ContainsKey(processorId) ? value : null;
            return Task.FromResult(result);
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            _checkpoints[processorId] = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
        {
            _checkpoints.Remove(processorId);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
        {
            _checkpoints[processorId] = position;
            return Task.CompletedTask;
        }

        public Task<bool> SaveIfLeaseHeldAsync(
            string processorId, long position, string consumerId, string replicaId,
            long fenceToken, bool useProcessorLeaseFencing = false, CancellationToken ct = default)
        {
            if (leaseHeld)
                _checkpoints[processorId] = position;

            return Task.FromResult(leaseHeld);
        }
    }

    /// <summary>
    /// IFencedCheckpointStore implementation that delegates SaveIfLeaseHeldAsync to
    /// the regular SaveAsync path, simulating a store that doesn't need to check leases
    /// but still satisfies the interface contract.
    /// </summary>
    private sealed class DelegatingFencedCheckpointStore : IFencedCheckpointStore
    {
        private readonly Dictionary<string, long> _checkpoints = new();

        public int RegularSaveCount { get; private set; }

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            _checkpoints.TryGetValue(processorId, out var value);
            long? result = _checkpoints.ContainsKey(processorId) ? value : null;
            return Task.FromResult(result);
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            _checkpoints[processorId] = position;
            RegularSaveCount++;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
        {
            _checkpoints.Remove(processorId);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
        {
            _checkpoints[processorId] = position;
            return Task.CompletedTask;
        }

        public async Task<bool> SaveIfLeaseHeldAsync(
            string processorId, long position, string consumerId, string replicaId,
            long fenceToken, bool useProcessorLeaseFencing = false, CancellationToken ct = default)
        {
            // Delegate to regular save — lease is always considered held in this implementation
            await SaveAsync(processorId, position, ct);
            return true;
        }
    }

    #endregion
}
