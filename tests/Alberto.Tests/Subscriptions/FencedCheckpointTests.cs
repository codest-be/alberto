using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

public class FencedCheckpointTests
{
    /// <summary>
    /// Verifies that a mock IFencedCheckpointStore that always reports the lease as held
    /// and delegates SaveIfLeaseHeldAsync to the regular SaveAsync path satisfies the
    /// interface contract end-to-end: the regular GetAsync path reflects the stored value.
    ///
    /// This documents the delegating-composition pattern — a store that does not need to
    /// check leases but still satisfies IFencedCheckpointStore. Not covered by the
    /// conformance specification, which only tests real adapters.
    /// </summary>
    [Fact]
    public async Task SaveIfLeaseHeldAsync_WhenNotImplemented_FallsBackToRegularSave()
    {
        var store = new DelegatingFencedCheckpointStore();

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId: "proc-2",
            position: 77,
            consumerId: "consumer-x",
            replicaId: "replica-x",
            fenceToken: 1,
            ct: TestContext.Current.CancellationToken);

        Assert.True(saved);
        var position = await store.GetAsync("proc-2", TestContext.Current.CancellationToken);
        Assert.Equal(77, position);
        Assert.Equal(1, store.RegularSaveCount);
    }

    #region Test Helpers

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
            await SaveAsync(processorId, position, ct);
            return true;
        }
    }

    #endregion
}
