using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Regression tests for P0.2 (COR-2): after a fenced-flush rejection the
/// <see cref="CachingCheckpointStore"/> must NOT serve the stale cached position.
/// The very next <c>GetAsync</c> call must reflect the last value that was actually
/// persisted to the underlying store, not the higher in-memory position that was
/// only queued for writing before the lease expired.
///
/// <para>
/// Failure scenario (pre-fix):
/// 1. Cache warms to persistedPosition from the inner store.
/// 2. <c>SaveAsync</c> advances the cache to cachedPosition (DB not yet written).
/// 3. <c>FlushAsync</c> is called; the fenced write is rejected because the lease expired.
/// 4. Pre-fix: <c>_cache</c> still holds cachedPosition.
///    <c>GetAsync</c> hits the cache and returns cachedPosition — stale.
///    Events between persistedPosition and cachedPosition are skipped forever.
/// 5. Post-fix: the cache entry is evicted on rejection.
///    <c>GetAsync</c> misses the cache and re-reads the inner store, returning
///    persistedPosition — correct.
/// </para>
/// </summary>
public class CachingCheckpointStoreFenceRejectionTests
{
    #region Core regression

    /// <summary>
    /// After a fenced flush is rejected, <c>GetAsync</c> must return the persisted
    /// DB position, not the higher position that was only ever held in memory.
    /// </summary>
    [Fact]
    public async Task FlushAsync_FencedWriteRejected_GetAsync_ReturnsPersistedPosition()
    {
        const string processorId = "proc-1";
        const long persistedPosition = 100L;
        const long cachedPosition   = 200L;

        // Arrange: inner store holds the last truly persisted position.
        var inner = new FencedCheckpointStore(leaseHeld: false);
        await inner.SaveAsync(processorId, persistedPosition, TestContext.Current.CancellationToken);

        // Timers disabled to keep the test deterministic.
        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // Warm the cache — GetAsync loads persistedPosition from the inner store.
        var beforeAdvance = await cache.GetAsync(processorId, TestContext.Current.CancellationToken);
        Assert.Equal(persistedPosition, beforeAdvance);

        // Processor advances: SaveAsync queues cachedPosition in memory without hitting the DB.
        await cache.SaveAsync(processorId, cachedPosition, TestContext.Current.CancellationToken);
        Assert.Equal(cachedPosition, await cache.GetAsync(processorId, TestContext.Current.CancellationToken));

        // Configure fencing context so FlushAsync uses lease-guarded writes.
        cache.SetFencingContext(new FencingContext("consumer-1", "replica-1"));

        // Act: flush is rejected — the lease has been lost.
        await cache.FlushAsync(TestContext.Current.CancellationToken);

        // Inner store must NOT have been updated (the fence blocked the write).
        Assert.Equal(persistedPosition, await inner.GetAsync(processorId, TestContext.Current.CancellationToken));

        // Assert: the cache entry must have been evicted on rejection.
        // Pre-fix this returns cachedPosition (200); post-fix it returns persistedPosition (100).
        var afterRejection = await cache.GetAsync(processorId, TestContext.Current.CancellationToken);
        Assert.Equal(persistedPosition, afterRejection);
    }

    #endregion

    #region Re-acquisition scenario

    /// <summary>
    /// After a fence rejection, the same replica re-acquires the lease. It must resume
    /// from the persisted DB position, not the lost in-memory advance. If it did not,
    /// events between persistedPosition and cachedPosition would be skipped forever.
    /// </summary>
    [Fact]
    public async Task AfterFenceRejection_ReacquiredLease_ResumesFromPersistedPosition()
    {
        const string processorId = "proc-1";
        const long persistedPosition = 50L;
        const long cachedPosition   = 150L;

        var inner = new FencedCheckpointStore(leaseHeld: false);
        await inner.SaveAsync(processorId, persistedPosition, TestContext.Current.CancellationToken);

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        cache.SetFencingContext(new FencingContext("consumer-1", "replica-1"));

        // Warm cache and advance without flushing.
        await cache.GetAsync(processorId, TestContext.Current.CancellationToken);
        await cache.SaveAsync(processorId, cachedPosition, TestContext.Current.CancellationToken);

        // Flush is rejected — lease was lost.
        await cache.FlushAsync(TestContext.Current.CancellationToken);

        // Replica re-acquires the lease; fenced store now accepts writes again.
        inner.LeaseHeld = true;

        // The control loop calls GetAsync to find where to resume after re-acquisition.
        var resumePosition = await cache.GetAsync(processorId, TestContext.Current.CancellationToken);

        // Must resume from the persisted position, not the lost in-memory advance.
        Assert.Equal(persistedPosition, resumePosition);
    }

    #endregion

    #region Callback + cache-eviction atomicity

    /// <summary>
    /// The <see cref="CachingCheckpointStore.OnFenceViolation"/> callback must fire AND
    /// the cache must be evicted. Both effects must occur for the same processor.
    /// </summary>
    [Fact]
    public async Task FlushAsync_FencedWriteRejected_InvokesOnFenceViolation_AndEvictsCache()
    {
        const string processorId = "proc-1";
        const long persistedPosition = 10L;
        const long cachedPosition   = 99L;

        var inner = new FencedCheckpointStore(leaseHeld: false);
        await inner.SaveAsync(processorId, persistedPosition, TestContext.Current.CancellationToken);

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        cache.SetFencingContext(new FencingContext("consumer-1", "replica-1"));

        var violatedIds = new List<string>();
        cache.OnFenceViolation = id => violatedIds.Add(id);

        await cache.GetAsync(processorId, TestContext.Current.CancellationToken);
        await cache.SaveAsync(processorId, cachedPosition, TestContext.Current.CancellationToken);

        await cache.FlushAsync(TestContext.Current.CancellationToken);

        // Callback must have fired.
        Assert.Contains(processorId, violatedIds);

        // Cache must also have been evicted — GetAsync re-reads from DB.
        var position = await cache.GetAsync(processorId, TestContext.Current.CancellationToken);
        Assert.Equal(persistedPosition, position);
    }

    #endregion

    #region Guard: successful flush must not evict cache

    /// <summary>
    /// A successful fenced write must NOT evict the cache; the value stays warm and the
    /// dirty flag is cleared as usual.
    /// </summary>
    [Fact]
    public async Task FlushAsync_FencedWriteAccepted_CacheRetainsUpdatedValue()
    {
        const string processorId = "proc-1";
        const long advancedPosition = 200L;

        var inner = new FencedCheckpointStore(leaseHeld: true);
        await inner.SaveAsync(processorId, 100L, TestContext.Current.CancellationToken);

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        cache.SetFencingContext(new FencingContext("consumer-1", "replica-1"));

        await cache.GetAsync(processorId, TestContext.Current.CancellationToken);
        await cache.SaveAsync(processorId, advancedPosition, TestContext.Current.CancellationToken);

        await cache.FlushAsync(TestContext.Current.CancellationToken);

        // After a successful flush the cache should still hold the written value.
        var cached = await cache.GetAsync(processorId, TestContext.Current.CancellationToken);
        Assert.Equal(advancedPosition, cached);

        // The inner store must also have been updated.
        var persisted = await inner.GetAsync(processorId, TestContext.Current.CancellationToken);
        Assert.Equal(advancedPosition, persisted);
    }

    #endregion

    #region Test helpers

    /// <summary>
    /// An in-memory implementation of <see cref="IFencedCheckpointStore"/> whose
    /// <see cref="LeaseHeld"/> flag controls whether fenced writes are accepted or rejected.
    /// </summary>
    private sealed class FencedCheckpointStore : IFencedCheckpointStore
    {
        private readonly Dictionary<string, long?> _data = new();

        /// <summary>Gets or sets whether <see cref="SaveIfLeaseHeldAsync"/> accepts writes.</summary>
        public bool LeaseHeld { get; set; }

        public FencedCheckpointStore(bool leaseHeld) => LeaseHeld = leaseHeld;

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            _data.TryGetValue(processorId, out var value);
            return Task.FromResult(value);
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            _data[processorId] = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
        {
            _data.Remove(processorId);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
        {
            _data[processorId] = position;
            return Task.CompletedTask;
        }

        public Task<bool> SaveIfLeaseHeldAsync(
            string processorId,
            long position,
            string consumerId,
            string replicaId,
            long fenceToken,
            bool useProcessorLeaseFencing = false,
            CancellationToken ct = default)
        {
            if (!LeaseHeld)
                return Task.FromResult(false);

            _data[processorId] = position;
            return Task.FromResult(true);
        }
    }

    #endregion
}
