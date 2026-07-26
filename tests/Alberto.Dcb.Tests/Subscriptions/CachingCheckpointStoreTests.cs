using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

public class CachingCheckpointStoreTests
{
    #region GetAsync Tests

    [Fact]
    public async Task GetAsync_NotCached_ShouldLoadFromInner()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner);

        var result = await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        Assert.Equal(100, result);
    }

    [Fact]
    public async Task GetAsync_Cached_ShouldReturnCachedValue()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner);

        // First call loads from inner
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        // Update inner directly
        await inner.SaveAsync("processor-1", 200, TestContext.Current.CancellationToken);

        // Second call should return cached value, not updated inner value
        var result = await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        Assert.Equal(100, result);
    }

    [Fact]
    public async Task GetAsync_NotFound_ShouldReturnNull()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner);

        var result = await cache.GetAsync("nonexistent", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_AfterSave_ShouldReturnSavedValue()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner);

        await cache.SaveAsync("processor-1", 50, TestContext.Current.CancellationToken);

        var result = await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        Assert.Equal(50, result);
    }

    #endregion

    #region SaveAsync Tests

    [Fact]
    public async Task SaveAsync_ShouldUpdateCacheImmediately()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        // Cache should have the value immediately
        var cached = await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);
        Assert.Equal(100, cached);

        // Inner should NOT have it yet (until flush)
        var fromInner = await inner.GetAsync("processor-1", TestContext.Current.CancellationToken);
        Assert.Null(fromInner);
    }

    [Fact]
    public async Task SaveAsync_MultipleSaves_ShouldKeepLatestValue()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await cache.SaveAsync("processor-1", 200, TestContext.Current.CancellationToken);
        await cache.SaveAsync("processor-1", 300, TestContext.Current.CancellationToken);

        var result = await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        Assert.Equal(300, result);
    }

    #endregion

    #region FlushAsync Tests

    [Fact]
    public async Task FlushAsync_ShouldWriteDirtyCheckpointsToInner()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await cache.SaveAsync("processor-2", 200, TestContext.Current.CancellationToken);

        await cache.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(100, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
        Assert.Equal(200, await inner.GetAsync("processor-2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlushAsync_EmptyDirtySet_ShouldBeNoop()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Should not throw when nothing to flush
        await cache.FlushAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FlushAsync_ShouldClearDirtyFlags()
    {
        var inner = new TrackingCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await cache.FlushAsync(TestContext.Current.CancellationToken);

        // Save count should be 1
        Assert.Equal(1, inner.SaveCount);

        // Flush again should not write again (not dirty)
        await cache.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.SaveCount);
    }

    [Fact]
    public async Task FlushAsync_WhenDirtyEntryWasExternallyReset_ShouldNotOverwriteReset()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Load the persisted checkpoint into the cache, then move forward without flushing.
        Assert.Equal(100, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
        await cache.SaveAsync("processor-1", 150, TestContext.Current.CancellationToken);

        // Simulate an operator reset while the cache still has a dirty pending write.
        await inner.SaveAsync("processor-1", 0, TestContext.Current.CancellationToken);

        await cache.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
        Assert.Equal(0, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlushAsync_WhenDirtyEntryWasExternallyDeleted_ShouldNotRecreateCheckpoint()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        Assert.Equal(100, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
        await cache.SaveAsync("processor-1", 150, TestContext.Current.CancellationToken);

        await inner.ResetAsync("processor-1", TestContext.Current.CancellationToken);

        await cache.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Null(await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    #endregion

    #region ResetAsync Tests

    [Fact]
    public async Task ResetAsync_ShouldClearCacheAndInner()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner);

        // Load into cache
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        await cache.ResetAsync("processor-1", TestContext.Current.CancellationToken);

        // Both cache and inner should be cleared
        Assert.Null(await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
        Assert.Null(await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetAsync_ShouldRemoveFromDirtySet()
    {
        var inner = new TrackingCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await cache.ResetAsync("processor-1", TestContext.Current.CancellationToken);
        await cache.FlushAsync(TestContext.Current.CancellationToken);

        // Should not have tried to save processor-1 (it was removed from dirty before flush)
        Assert.Equal(0, inner.SaveCount);
    }

    #endregion

    #region DisposeAsync Tests

    [Fact]
    public async Task DisposeAsync_ShouldFlushRemainingCheckpoints()
    {
        var inner = new InMemoryCheckpointStore();
        var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        // Dispose should flush
        await cache.DisposeAsync();

        Assert.Equal(100, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_ShouldNotThrow()
    {
        var inner = new InMemoryCheckpointStore();
        var cache = new CachingCheckpointStore(inner);

        await cache.DisposeAsync();
        await cache.DisposeAsync(); // Should not throw
    }

    #endregion

    #region Timer-Based Flush Tests

    [Fact]
    public async Task TimerFlush_ShouldFlushPeriodically()
    {
        var inner = new InMemoryCheckpointStore();
        var time = new FakeTimeProvider();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromMilliseconds(50), timeProvider: time);

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMilliseconds(50));

        await WaitForInnerAsync(inner, "processor-1", 100);

        Assert.Equal(100, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Flush_WhenIntervalElapses_WritesThroughToInner_WithoutSleeping()
    {
        var inner = new InMemoryCheckpointStore();
        var time = new FakeTimeProvider();
        await using var store = new CachingCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromSeconds(30),
            resyncInterval: TimeSpan.FromMinutes(5),
            timeProvider: time);

        await store.SaveAsync("proc-1", 42, TestContext.Current.CancellationToken);

        // Nothing has reached the inner store yet: the write is only cached.
        Assert.Null(await inner.GetAsync("proc-1", TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromSeconds(30));

        // The timer callback is async void, so yield until it has run rather than
        // asserting immediately. This is a scheduling yield, not a wall-clock wait.
        await WaitForInnerAsync(inner, "proc-1", 42);

        Assert.Equal(42, await inner.GetAsync("proc-1", TestContext.Current.CancellationToken));
    }

    private static async Task WaitForInnerAsync(ICheckpointStore inner, string processorId, long expected)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await inner.GetAsync(processorId, TestContext.Current.CancellationToken) == expected) return;
            await Task.Yield();
        }
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task ConcurrentSaves_ShouldNotLoseData()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        var tasks = Enumerable.Range(1, 100)
            .Select(i => cache.SaveAsync($"processor-{i % 10}", i))
            .ToList();

        await Task.WhenAll(tasks);
        await cache.FlushAsync(TestContext.Current.CancellationToken);

        // All 10 processors should have values
        for (int i = 0; i < 10; i++)
        {
            var value = await inner.GetAsync($"processor-{i}", TestContext.Current.CancellationToken);
            Assert.NotNull(value);
        }
    }

    #endregion

    #region Resync Tests

    [Fact]
    public async Task ResyncFromStore_ExternalReset_ShouldUpdateCache()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 500, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Load into cache
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);
        Assert.Equal(500, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));

        // Simulate external reset in DB
        await inner.SaveAsync("processor-1", 0, TestContext.Current.CancellationToken);

        // Resync should detect the reset
        await cache.ResyncFromStoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResyncFromStore_ExternalDelete_ShouldUpdateCache()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 500, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Load into cache
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        // Simulate external delete in DB
        await inner.ResetAsync("processor-1", TestContext.Current.CancellationToken);

        // Resync should detect the deletion
        await cache.ResyncFromStoreAsync(TestContext.Current.CancellationToken);

        Assert.Null(await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResyncFromStore_DirtyEntry_ShouldNotOverwrite()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Save via cache (marks as dirty)
        await cache.SaveAsync("processor-1", 500, TestContext.Current.CancellationToken);

        // Inner still has nothing — but the dirty flag should protect the cache
        await cache.ResyncFromStoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(500, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResyncFromStore_NoExternalChange_ShouldNotAffectCache()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 500, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Load into cache and flush so it's clean
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        // Resync when DB value matches — should be a no-op
        await cache.ResyncFromStoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(500, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResyncFromStore_DbValueHigher_ShouldNotAffectCache()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 500, TestContext.Current.CancellationToken);
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Load into cache
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        // Simulate DB moving ahead (e.g. another replica wrote)
        await inner.SaveAsync("processor-1", 1000, TestContext.Current.CancellationToken);

        // Resync should NOT overwrite — DB ahead is normal, not a reset
        await cache.ResyncFromStoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(500, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResyncTimer_ShouldDetectExternalReset()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 500, TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(50), timeProvider: time);

        // Load into cache
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        // Simulate external reset
        await inner.SaveAsync("processor-1", 0, TestContext.Current.CancellationToken);

        // Advance to trigger the resync timer
        time.Advance(TimeSpan.FromMilliseconds(50));

        await WaitForInnerAsync(cache, "processor-1", 0);

        Assert.Equal(0, await cache.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    #endregion

    #region Test Helpers

    private sealed class InMemoryCheckpointStore : ICheckpointStore
    {
        private readonly Dictionary<string, long?> _checkpoints = new();

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            _checkpoints.TryGetValue(processorId, out var value);
            return Task.FromResult(value);
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
    }

    private sealed class TrackingCheckpointStore : ICheckpointStore
    {
        private readonly Dictionary<string, long?> _checkpoints = new();

        public int SaveCount { get; private set; }

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            _checkpoints.TryGetValue(processorId, out var value);
            return Task.FromResult(value);
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            _checkpoints[processorId] = position;
            SaveCount++;
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
    }

    #endregion

    #region Concurrent read/write Tests

    /// <summary>
    /// A checkpoint store whose reads block until the test releases them, so a write can be
    /// made to land in the middle of one.
    /// </summary>
    private sealed class GatedCheckpointStore : ICheckpointStore
    {
        private readonly InMemoryCheckpointStore _inner = new();

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            ReadStarted.TrySetResult();
            await ReleaseRead.Task;
            return await _inner.GetAsync(processorId, ct);
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
            => _inner.SaveAsync(processorId, position, ct);

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => _inner.ResetAsync(processorId, ct);

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => _inner.RewindAsync(processorId, position, ct);
    }

    /// <summary>
    /// Two callers share one store: a control loop saving its progress, and anything else that
    /// reads the same processor's checkpoint — the rebuild coordinator polling a shadow loop, say.
    /// Seeding the cache from a read that started before the save must not roll the save back,
    /// because the loop re-streams from its checkpoint on every pass and would deliver the whole
    /// batch a second time. A projection that counts would then count it twice.
    /// </summary>
    [Fact]
    public async Task GetAsync_WhenASaveLandsMidRead_DoesNotRollTheCheckpointBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new GatedCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // A reader misses the cache and goes to the database, which has no row for this processor.
        var read = cache.GetAsync("processor-1", ct);
        await inner.ReadStarted.Task;

        // While that read is in flight, the loop processes a batch and records position 4.
        await cache.SaveAsync("processor-1", 4, ct);

        inner.ReleaseRead.TrySetResult();
        await read;

        Assert.Equal(4, await cache.GetAsync("processor-1", ct));
    }

    #endregion
}
