using Alberto.Dcb.Subscriptions;
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
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromMilliseconds(50));

        await cache.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        // Wait for timer to trigger flush
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(100, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
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
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(50));

        // Load into cache
        await cache.GetAsync("processor-1", TestContext.Current.CancellationToken);

        // Simulate external reset
        await inner.SaveAsync("processor-1", 0, TestContext.Current.CancellationToken);

        // Wait for resync timer to fire
        await Task.Delay(200, TestContext.Current.CancellationToken);

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
    }

    #endregion
}
