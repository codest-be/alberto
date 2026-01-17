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
        await inner.SaveAsync("processor-1", 100);
        await using var cache = new CachingCheckpointStore(inner);

        var result = await cache.GetAsync("processor-1");

        Assert.Equal(100, result);
    }

    [Fact]
    public async Task GetAsync_Cached_ShouldReturnCachedValue()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100);
        await using var cache = new CachingCheckpointStore(inner);

        // First call loads from inner
        await cache.GetAsync("processor-1");

        // Update inner directly
        await inner.SaveAsync("processor-1", 200);

        // Second call should return cached value, not updated inner value
        var result = await cache.GetAsync("processor-1");

        Assert.Equal(100, result);
    }

    [Fact]
    public async Task GetAsync_NotFound_ShouldReturnNull()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner);

        var result = await cache.GetAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_AfterSave_ShouldReturnSavedValue()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner);

        await cache.SaveAsync("processor-1", 50);

        var result = await cache.GetAsync("processor-1");

        Assert.Equal(50, result);
    }

    #endregion

    #region SaveAsync Tests

    [Fact]
    public async Task SaveAsync_ShouldUpdateCacheImmediately()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100);

        // Cache should have the value immediately
        var cached = await cache.GetAsync("processor-1");
        Assert.Equal(100, cached);

        // Inner should NOT have it yet (until flush)
        var fromInner = await inner.GetAsync("processor-1");
        Assert.Null(fromInner);
    }

    [Fact]
    public async Task SaveAsync_MultipleSaves_ShouldKeepLatestValue()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100);
        await cache.SaveAsync("processor-1", 200);
        await cache.SaveAsync("processor-1", 300);

        var result = await cache.GetAsync("processor-1");

        Assert.Equal(300, result);
    }

    #endregion

    #region FlushAsync Tests

    [Fact]
    public async Task FlushAsync_ShouldWriteDirtyCheckpointsToInner()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100);
        await cache.SaveAsync("processor-2", 200);

        await cache.FlushAsync();

        Assert.Equal(100, await inner.GetAsync("processor-1"));
        Assert.Equal(200, await inner.GetAsync("processor-2"));
    }

    [Fact]
    public async Task FlushAsync_EmptyDirtySet_ShouldBeNoop()
    {
        var inner = new InMemoryCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        // Should not throw when nothing to flush
        await cache.FlushAsync();
    }

    [Fact]
    public async Task FlushAsync_ShouldClearDirtyFlags()
    {
        var inner = new TrackingCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100);
        await cache.FlushAsync();

        // Save count should be 1
        Assert.Equal(1, inner.SaveCount);

        // Flush again should not write again (not dirty)
        await cache.FlushAsync();

        Assert.Equal(1, inner.SaveCount);
    }

    #endregion

    #region ResetAsync Tests

    [Fact]
    public async Task ResetAsync_ShouldClearCacheAndInner()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100);
        await using var cache = new CachingCheckpointStore(inner);

        // Load into cache
        await cache.GetAsync("processor-1");

        await cache.ResetAsync("processor-1");

        // Both cache and inner should be cleared
        Assert.Null(await cache.GetAsync("processor-1"));
        Assert.Null(await inner.GetAsync("processor-1"));
    }

    [Fact]
    public async Task ResetAsync_ShouldRemoveFromDirtySet()
    {
        var inner = new TrackingCheckpointStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1));

        await cache.SaveAsync("processor-1", 100);
        await cache.ResetAsync("processor-1");
        await cache.FlushAsync();

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

        await cache.SaveAsync("processor-1", 100);

        // Dispose should flush
        await cache.DisposeAsync();

        Assert.Equal(100, await inner.GetAsync("processor-1"));
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

        await cache.SaveAsync("processor-1", 100);

        // Wait for timer to trigger flush
        await Task.Delay(200);

        Assert.Equal(100, await inner.GetAsync("processor-1"));
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
        await cache.FlushAsync();

        // All 10 processors should have values
        for (int i = 0; i < 10; i++)
        {
            var value = await inner.GetAsync($"processor-{i}");
            Assert.NotNull(value);
        }
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
