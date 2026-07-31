using Alberto;
using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests that <see cref="EventStoreHead"/> clamps the contiguous head by the
/// backend's in-flight visibility barrier (<see cref="IEventStoreHeadBackend.GetStableHeadAsync"/>)
/// and wakes early on an append signal.
/// </summary>
public sealed class EventStoreHeadBarrierTests
{
    [Fact]
    public async Task Refresh_ClampsHeadToStableBarrier()
    {
        // Contiguous positions would put the head at 3, but the barrier says only
        // position 2 is safe to expose (position 3's transaction is still in flight).
        var backend = new FakeHeadBackend { Positions = [1, 2, 3], StableHead = 2 };
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token); // performs one synchronous refresh

        Assert.Equal(2, head.Current);

        await head.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_NoBarrier_UsesContiguousHead()
    {
        // long.MaxValue barrier = "no clamp" → head follows the contiguous scan.
        var backend = new FakeHeadBackend { Positions = [1, 2, 3], StableHead = long.MaxValue };
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);

        Assert.Equal(3, head.Current);

        await head.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Signal_WakesHeadBeforePollingInterval()
    {
        // A long interval ensures the advance can only come from the signal.
        var backend = new FakeHeadBackend { Positions = [], StableHead = long.MaxValue };
        var signal = new EventAppendedSignal();
        var head = new EventStoreHead(backend, TimeSpan.FromSeconds(30), signal: signal);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        Assert.Equal(0, head.Current);

        backend.Positions = [1, 2, 3];
        signal.Signal();

        // Should advance well within the 30s interval.
        var advanced = await WaitForAsync(() => head.Current == 3, TimeSpan.FromSeconds(5));
        Assert.True(advanced, $"head did not advance on signal; Current={head.Current}");

        await head.StopAsync(CancellationToken.None);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private sealed class FakeHeadBackend : IEventStoreHeadBackend
    {
        public IReadOnlyList<long> Positions { get; set; } = [];
        public long StableHead { get; set; } = long.MaxValue;

        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        {
            var ceiling = afterPosition + windowSize;
            IReadOnlyList<long> window = Positions
                .Where(p => p > afterPosition && p <= ceiling)
                .OrderBy(p => p)
                .ToList();
            return Task.FromResult(window);
        }

        public Task<long> GetStableHeadAsync(long afterPosition, CancellationToken cancellationToken = default)
            => Task.FromResult(StableHead);
    }
}
