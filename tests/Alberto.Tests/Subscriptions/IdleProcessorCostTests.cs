using System.Collections.Concurrent;
using Alberto.Checkpoints;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// What an idle or uninterested processor costs (codest-be/alberto#268): the head records the
/// types it saw so a loop skips a read that would come back empty, a caught-up loop waits for the
/// head rather than a timer, the idle head poll backs off while LISTEN is healthy, and skip-forward
/// checkpoints ride one batched flush.
/// </summary>
public sealed class IdleProcessorCostTests
{
    private static EventToPersist Event(string type) => new() { EventType = new EventType(type), Tags = [], EventData = "{}" };

    private static async Task Append(InMemoryEventStoreBackend backend, params string[] types)
    {
        foreach (var type in types)
            await backend.AppendAsync([Event(type)], cancellationToken: TestContext.Current.CancellationToken);
    }

    private static HashSet<string> Types(params string[] types) => new(types, StringComparer.Ordinal);

    // ── EventStoreHead.HasNoneOf ────────────────────────────────────────────

    [Fact]
    public async Task HasNoneOf_answers_per_type_from_every_window_the_head_crossed()
    {
        var backend = new InMemoryEventStoreBackend();
        await Append(backend, "a", "b", "b", "b", "b"); // positions 1..5
        var head = new EventStoreHead(backend, windowSize: 2);

        await head.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, head.Current);
        Assert.True(head.HasNoneOf(Types("c"), 0));
        Assert.False(head.HasNoneOf(Types("a"), 0));
        // A loop far behind the last window: "a" sat in the first window only.
        Assert.True(head.HasNoneOf(Types("a"), 1));
        Assert.False(head.HasNoneOf(Types("a", "b"), 1));
        Assert.False(head.HasNoneOf(Types("b"), 4));
        Assert.True(head.HasNoneOf(Types("b"), 5));
    }

    [Fact]
    public async Task HasNoneOf_keeps_types_from_earlier_refreshes()
    {
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, windowSize: 2);
        await Append(backend, "a");
        await head.RefreshAsync(TestContext.Current.CancellationToken);
        await Append(backend, "b", "b", "b");
        await head.RefreshAsync(TestContext.Current.CancellationToken);

        // The loop is still at 0: "a" was only in a refresh before the latest one.
        Assert.False(head.HasNoneOf(Types("a"), 0));
        Assert.True(head.HasNoneOf(Types("a"), 1));
    }

    [Fact]
    public async Task HasNoneOf_says_no_for_an_empty_set_or_a_backend_that_reports_no_types()
    {
        var backend = new InMemoryEventStoreBackend();
        await Append(backend, "a");
        var head = new EventStoreHead(backend);
        await head.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(head.HasNoneOf(Types(), 0));

        var positionsOnly = new EventStoreHead(new PositionsOnlyBackend([1, 2, 3]));
        await positionsOnly.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, positionsOnly.Current);
        Assert.False(positionsOnly.HasNoneOf(Types("anything"), 0));
    }

    /// <summary>A third-party head backend without the snapshot capability.</summary>
    private sealed class PositionsOnlyBackend(IReadOnlyList<long> positions) : IEventStoreHeadBackend
    {
        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>(positions.Where(p => p > afterPosition).Take(windowSize).ToList());
    }

    [Fact]
    public async Task HasNoneOf_sees_a_type_that_committed_under_a_barrier_hold()
    {
        // Position 2 is in flight in the first snapshot: the barrier holds the head at 1 while 3
        // is already visible. 2 then commits with the wanted type.
        var backend = new ScriptedSnapshots(
            new HeadSnapshot(new HeadWindow([1, 3], null), 1, new Dictionary<string, long> { ["other"] = 3 }),
            new HeadSnapshot(new HeadWindow([2, 3], null), long.MaxValue, new Dictionary<string, long> { ["wanted"] = 2, ["other"] = 3 }));
        var head = new EventStoreHead(backend);
        var ct = TestContext.Current.CancellationToken;

        await head.RefreshAsync(ct);
        Assert.Equal(1, head.Current);
        await head.RefreshAsync(ct);

        Assert.Equal(3, head.Current);
        Assert.False(head.HasNoneOf(Types("wanted"), 1));
        Assert.True(head.HasNoneOf(Types("wanted"), 2));
    }

    private sealed class ScriptedSnapshots(params HeadSnapshot[] script) : IEventStoreHeadBackend, IHeadSnapshotBackend
    {
        private int _next;

        public Task<HeadSnapshot> GetHeadSnapshotAsync(long afterPosition, int windowSize, CancellationToken cancellationToken)
            => Task.FromResult(script[Math.Min(_next++, script.Length - 1)]);

        public Task<IReadOnlyList<long>> GetPositionsAsync(long afterPosition, int windowSize, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The head reads snapshots.");
    }

    // ── EventStoreHead.Advanced and the idle poll ───────────────────────────

    [Fact]
    public async Task Advanced_completes_when_the_head_moves_and_not_before()
    {
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend);
        var ct = TestContext.Current.CancellationToken;

        var first = head.Advanced;
        await head.RefreshAsync(ct);
        Assert.False(first.IsCompleted);

        await Append(backend, "a");
        await head.RefreshAsync(ct);
        Assert.True(first.IsCompleted);

        var second = head.Advanced;
        await head.RefreshAsync(ct);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public void The_idle_poll_backs_off_only_while_listening_and_never_past_a_slower_interval()
    {
        var fast = TimeSpan.FromMilliseconds(100);
        var gap = TimeSpan.FromMilliseconds(10);

        Assert.Equal(EventStoreHead.IdleWaitWhileListening, EventStoreHead.NextWait(fast, TimeSpan.Zero, listening: true));
        Assert.Equal(fast, EventStoreHead.NextWait(fast, TimeSpan.Zero, listening: false));
        Assert.Equal(gap, EventStoreHead.NextWait(fast, gap, listening: true));
        Assert.Equal(TimeSpan.FromSeconds(5), EventStoreHead.NextWait(TimeSpan.FromSeconds(5), TimeSpan.Zero, listening: true));
    }

    [Fact]
    public async Task The_head_backs_off_while_the_listener_listens_and_not_while_a_barrier_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        var interval = TimeSpan.FromMilliseconds(100);
        var signal = new EventAppendedSignal();
        var backend = new ScriptedSnapshots(
            new HeadSnapshot(new HeadWindow([1, 2], null), 1, new Dictionary<string, long>()),
            new HeadSnapshot(new HeadWindow([2], null), long.MaxValue, new Dictionary<string, long>()));
        var head = new EventStoreHead(backend, interval, signal: signal);

        Assert.Equal(interval, head.NextRefreshWait);
        signal.IsListening = true;
        Assert.Equal(EventStoreHead.IdleWaitWhileListening, head.NextRefreshWait);

        // A hold ends without a NOTIFY, so the head keeps looking at the configured interval.
        await head.RefreshAsync(ct);
        Assert.Equal(interval, head.NextRefreshWait);

        await head.RefreshAsync(ct);
        Assert.Equal(2, head.Current);
        Assert.Equal(EventStoreHead.IdleWaitWhileListening, head.NextRefreshWait);

        signal.IsListening = false;
        Assert.Equal(interval, head.NextRefreshWait);
    }

    [Fact]
    public async Task The_registered_head_tracks_types_through_the_module_backend()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddAlberto("idle", module => module.WithInMemory().WithControlLoop());
        await using var provider = services.BuildServiceProvider();
        var backend = provider.GetRequiredKeyedService<IEventStoreBackend>("idle");
        var head = provider.GetRequiredKeyedService<EventStoreHead>("idle");

        await backend.AppendAsync([Event("present")], cancellationToken: ct);
        await head.RefreshAsync(ct);

        Assert.True(head.HasNoneOf(Types("absent"), 0));
        Assert.False(head.HasNoneOf(Types("present"), 0));
    }

    // ── ControlLoop ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_loop_skips_reads_of_absent_types_and_wakes_from_the_head(bool pipelined)
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new CountingBackend();
        var checkpoints = new InMemoryCheckpointStore();
        await Append(backend.Inner, "other", "other", "other");

        var head = new EventStoreHead(backend.Inner, TimeSpan.FromMilliseconds(10));
        var processor = new Recorder(Types("wanted"));
        // An hour-long polling interval: only the head can wake the loop within the test.
        var loop = new ControlLoop(processor, head, backend, checkpoints, TimeSpan.FromHours(1), 100,
            executionOptions: pipelined ? new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Required, MaxConcurrency = 2 } : null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        try
        {
            await Until(async () => await checkpoints.GetAsync(processor.ProcessorId, ct) == 3, ct);
            Assert.Equal(0, backend.TypedReads);

            await Append(backend.Inner, "wanted");
            await Until(() => Task.FromResult(processor.Count == 1), ct);
            Assert.Equal(1, backend.TypedReads);
        }
        finally
        {
            await cts.CancelAsync();
            await loop.StopAsync(CancellationToken.None);
            await head.StopAsync(CancellationToken.None);
        }
    }

    private static async Task Until(Func<Task<bool>> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class Recorder(IReadOnlySet<string> types) : IBatchableProcessor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public string ProcessorId => "idle-cost";
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes => types;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref _count, events.Count);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingBackend : IEventStoreBackend
    {
        private int _typedReads;
        public InMemoryEventStoreBackend Inner { get; } = new();
        public int TypedReads => Volatile.Read(ref _typedReads);

        public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllOfTypesAsync(
            IReadOnlySet<string> eventTypes, long afterPosition, int limit, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _typedReads);
            return Inner.ReadAllOfTypesAsync(eventTypes, afterPosition, limit, cancellationToken);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(IEnumerable<IEventToPersist> events,
            DcbQuery? dcbQuery = null, long? expectedPosition = null, CancellationToken cancellationToken = default)
            => Inner.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken);

        public Task<IReadOnlyCollection<IEventEnvelope>> ReadAsync(DcbQuery query, long afterPosition, int limit,
            CancellationToken cancellationToken = default) => Inner.ReadAsync(query, afterPosition, limit, cancellationToken);

        public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllAsync(long afterPosition, int limit,
            CancellationToken cancellationToken = default) => Inner.ReadAllAsync(afterPosition, limit, cancellationToken);

        public IAsyncEnumerable<IEventEnvelope> StreamAsync(DcbQuery query, long afterPosition = 0,
            CancellationToken cancellationToken = default) => Inner.StreamAsync(query, afterPosition, cancellationToken);

        public IAsyncEnumerable<IEventEnvelope> StreamAllAsync(long afterPosition = 0,
            CancellationToken cancellationToken = default) => Inner.StreamAllAsync(afterPosition, cancellationToken);

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
            => Inner.GetLastPositionAsync(cancellationToken);
    }

    // ── CachingCheckpointStore.SaveSkipForwardAsync ─────────────────────────

    [Fact]
    public async Task Skip_forward_saves_under_a_lease_ride_one_flush_shortly_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new CountingFencedStore();
        var time = new FakeTimeProvider();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1), timeProvider: time);
        cache.SetFencingContext(new FencingContext("consumer", "replica", true, _ => 7));

        await cache.SaveSkipForwardAsync("p1", 10);
        await cache.SaveSkipForwardAsync("p2", 10);
        await cache.SaveSkipForwardAsync("p1", 12);

        Assert.Equal(12, await cache.GetAsync("p1", ct));
        Assert.Equal(0, inner.Writes);

        time.Advance(CachingCheckpointStore.SkipForwardFlushDelay);
        await Until(() => Task.FromResult(inner.Stored.Count == 2), ct);

        Assert.Equal(12, inner.Stored["p1"]);
        Assert.Equal(10, inner.Stored["p2"]);
        Assert.Equal(2, inner.Writes);
    }

    [Fact]
    public async Task Skip_forward_saves_without_a_lease_wait_for_the_regular_flush()
    {
        var inner = new CountingFencedStore();
        var time = new FakeTimeProvider();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1), timeProvider: time);

        await cache.SaveSkipForwardAsync("p1", 10);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();

        Assert.Equal(0, inner.Writes);
        Assert.False(cache.HoldsLease("p1"));
    }

    private sealed class CountingFencedStore : IFencedCheckpointStore
    {
        private int _writes;
        public ConcurrentDictionary<string, long> Stored { get; } = new();
        public int Writes => Volatile.Read(ref _writes);

        public Task<long?> GetAsync(string processorId, CancellationToken cancellationToken = default)
            => Task.FromResult(Stored.TryGetValue(processorId, out var p) ? p : (long?)null);

        public Task SaveAsync(string processorId, long position, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            Stored[processorId] = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken cancellationToken = default)
        {
            Stored.TryRemove(processorId, out _);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken cancellationToken = default)
        {
            Stored[processorId] = position;
            return Task.CompletedTask;
        }

        public Task<bool> SaveIfLeaseHeldAsync(string processorId, long position, string consumerId, string replicaId,
            long fenceToken, bool useProcessorLeaseFencing = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            Stored[processorId] = position;
            return Task.FromResult(true);
        }
    }
}
