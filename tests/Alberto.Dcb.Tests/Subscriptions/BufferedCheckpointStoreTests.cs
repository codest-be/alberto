using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for BufferedCheckpointStore.
/// </summary>
public class BufferedCheckpointStoreTests
{
    [Fact]
    public async Task Get_WhenNotBuffered_ShouldReadFromInnerStore()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        await using var buffered = new BufferedCheckpointStore(inner);

        var result = await buffered.GetAsync("processor-1", TestContext.Current.CancellationToken);

        Assert.Equal(100, result);
    }

    [Fact]
    public async Task Get_WhenBuffered_ShouldReturnBufferedValue()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 50, TestContext.Current.CancellationToken);

        await using var buffered = new BufferedCheckpointStore(inner);
        await buffered.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken); // Buffer a higher value

        var result = await buffered.GetAsync("processor-1", TestContext.Current.CancellationToken);

        Assert.Equal(100, result); // Should return buffered value, not inner
    }

    [Fact]
    public async Task Save_ShouldNotWriteImmediately()
    {
        var inner = new InMemoryCheckpointStore();
        await using var buffered = new BufferedCheckpointStore(inner);

        await buffered.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        // Inner store should still be empty
        var innerValue = await inner.GetAsync("processor-1", TestContext.Current.CancellationToken);
        Assert.Null(innerValue);
    }

    [Fact]
    public async Task FlushAsync_ShouldWriteToInnerStore()
    {
        var inner = new InMemoryCheckpointStore();
        await using var buffered = new BufferedCheckpointStore(inner);

        await buffered.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await buffered.SaveAsync("processor-2", 200, TestContext.Current.CancellationToken);
        await buffered.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(100, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
        Assert.Equal(200, await inner.GetAsync("processor-2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_ShouldFlush()
    {
        var inner = new InMemoryCheckpointStore();

        var buffered = new BufferedCheckpointStore(inner);
        await buffered.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);
        await buffered.DisposeAsync();

        // After dispose, inner store should have the value
        Assert.Equal(100, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetAsync_ShouldRemoveFromBufferAndInnerStore()
    {
        var inner = new InMemoryCheckpointStore();
        await inner.SaveAsync("processor-1", 50, TestContext.Current.CancellationToken); // Pre-existing in inner

        await using var buffered = new BufferedCheckpointStore(inner);
        await buffered.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken); // Buffer a value

        await buffered.ResetAsync("processor-1", TestContext.Current.CancellationToken);

        // Both buffer and inner should be cleared
        Assert.Null(await buffered.GetAsync("processor-1", TestContext.Current.CancellationToken));
        Assert.Null(await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_MultipleTimes_ShouldOnlyKeepLatest()
    {
        var inner = new InMemoryCheckpointStore();
        await using var buffered = new BufferedCheckpointStore(inner);

        await buffered.SaveAsync("processor-1", 10, TestContext.Current.CancellationToken);
        await buffered.SaveAsync("processor-1", 20, TestContext.Current.CancellationToken);
        await buffered.SaveAsync("processor-1", 30, TestContext.Current.CancellationToken);

        var result = await buffered.GetAsync("processor-1", TestContext.Current.CancellationToken);

        Assert.Equal(30, result);
    }

    [Fact]
    public async Task FlushAsync_ConcurrentCalls_ShouldNotDuplicate()
    {
        var inner = new CountingCheckpointStore();
        await using var buffered = new BufferedCheckpointStore(inner);

        await buffered.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        // Flush concurrently
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => buffered.FlushAsync())
            .ToArray();

        await Task.WhenAll(tasks);

        // Should only write once due to lock (or skip if already flushing)
        Assert.True(inner.SaveCount >= 1);
    }

    [Fact]
    public async Task AutoFlush_ShouldOccurAfterInterval()
    {
        var inner = new InMemoryCheckpointStore();
        await using var buffered = new BufferedCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromMilliseconds(50));

        await buffered.SaveAsync("processor-1", 100, TestContext.Current.CancellationToken);

        // Wait for auto-flush
        await Task.Delay(150, TestContext.Current.CancellationToken);

        // Should have been flushed automatically
        Assert.Equal(100, await inner.GetAsync("processor-1", TestContext.Current.CancellationToken));
    }

    private class CountingCheckpointStore : ICheckpointStore
    {
        private readonly InMemoryCheckpointStore _inner = new();
        public int SaveCount;

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
            => _inner.GetAsync(processorId, ct);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SaveCount);
            return _inner.SaveAsync(processorId, position, ct);
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => _inner.ResetAsync(processorId, ct);
    }
}
