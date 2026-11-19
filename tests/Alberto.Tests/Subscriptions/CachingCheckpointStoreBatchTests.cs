using Alberto.Checkpoints;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// <see cref="CachingCheckpointStore"/> over a store with <see cref="ICheckpointBatchStore"/>:
/// a leased save, a flush and a resync each cost one round trip, and the outcome of a write
/// (written, reset, fenced, retry) is applied per processor.
/// </summary>
public class CachingCheckpointStoreBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CachingCheckpointStore Create(BatchStore inner) =>
        new(inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

    private static FencingContext Leased => new("consumer-1", "replica-1", UseProcessorLeaseFencing: true, _ => 7);

    [Fact]
    public async Task LeasedSave_AfterFirstWrite_IsOneRoundTrip_WithTheFloorAndFenceToken()
    {
        var inner = new BatchStore();
        await using var cache = Create(inner);
        cache.SetFencingContext(Leased);

        await cache.SaveAsync("p", 10, Ct);
        inner.Reset();

        await cache.SaveAsync("p", 20, Ct);

        inner.RoundTrips.Should().Be(1, because: "reset detection is folded into the fenced write");
        inner.Gets.Should().Be(0);
        inner.LastWrites.Should().ContainSingle()
            .Which.Should().Be(new CheckpointWrite("p", 20, Floor: 10, FenceToken: 7));
        inner.LastFencing.Should().NotBeNull();
        inner.Stored("p").Should().Be(20);
    }

    [Fact]
    public async Task Flush_OfManyProcessors_IsOneWrite()
    {
        var inner = new BatchStore();
        await using var cache = Create(inner);

        for (var i = 0; i < 5; i++)
            await cache.SaveAsync($"p{i}", 100 + i, Ct);
        await cache.FlushAsync(Ct);

        inner.RoundTrips.Should().Be(1);
        inner.LastWrites.Should().HaveCount(5);
        inner.LastFencing.Should().BeNull();
        inner.Stored("p3").Should().Be(103);
    }

    [Fact]
    public async Task ResetOutcome_LowersTheCache_DropsTheWrite_AndIsNotAFenceViolation()
    {
        var inner = new BatchStore();
        await using var cache = Create(inner);
        cache.SetFencingContext(Leased);
        var violations = 0;
        cache.OnFenceViolation = _ => violations++;

        await cache.SaveAsync("p", 100, Ct);
        inner.Outcome = _ => new(CheckpointWriteResult.Reset, 40);

        await cache.SaveAsync("p", 150, Ct);

        (await cache.GetAsync("p", Ct)).Should().Be(40);
        violations.Should().Be(0);

        // The pending write was dropped, not kept for the next flush.
        inner.Reset();
        await cache.FlushAsync(Ct);
        inner.RoundTrips.Should().Be(0);
    }

    [Fact]
    public async Task FencedOutcome_EvictsTheCache_AndRaisesTheFenceViolation()
    {
        var inner = new BatchStore();
        await using var cache = Create(inner);
        cache.SetFencingContext(Leased);
        string? violated = null;
        cache.OnFenceViolation = id => violated = id;

        await cache.SaveAsync("p", 100, Ct);
        inner.Outcome = _ => new(CheckpointWriteResult.Fenced);

        await cache.SaveAsync("p", 150, Ct);

        violated.Should().Be("p");
        (await cache.GetAsync("p", Ct)).Should().Be(100, because: "the cache re-reads the store");
    }

    [Fact]
    public async Task RetryOutcome_KeepsTheEntryDirty_ForTheNextFlush()
    {
        var inner = new BatchStore();
        await using var cache = Create(inner);
        inner.Outcome = _ => new(CheckpointWriteResult.Retry);

        await cache.SaveAsync("p", 100, Ct);
        await cache.FlushAsync(Ct);
        inner.Stored("p").Should().BeNull();

        inner.Outcome = null;
        await cache.FlushAsync(Ct);
        inner.Stored("p").Should().Be(100);
    }

    [Fact]
    public async Task Resync_ReadsEveryCachedProcessorInOneCall_AndAppliesAnExternalReset()
    {
        var inner = new BatchStore();
        await using var cache = Create(inner);
        for (var i = 0; i < 3; i++)
            await cache.SaveAsync($"p{i}", 100, Ct);
        await cache.FlushAsync(Ct);

        inner.Set("p1", 30);
        inner.Reset();
        await cache.ResyncFromStoreAsync(Ct);

        inner.Gets.Should().Be(1);
        (await cache.GetAsync("p1", Ct)).Should().Be(30);
        (await cache.GetAsync("p0", Ct)).Should().Be(100);
    }

    [Fact]
    public async Task WriteThrough_OfOneProcessor_DoesNotWaitOnAnother()
    {
        var inner = new BatchStore();
        await using var cache = Create(inner);
        cache.SetFencingContext(Leased);
        var blocked = new TaskCompletionSource();
        inner.Gate = w => w.ProcessorId == "slow" ? blocked.Task : Task.CompletedTask;

        var slow = cache.SaveAsync("slow", 1, Ct);
        await cache.SaveAsync("fast", 1, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        slow.IsCompleted.Should().BeFalse();
        blocked.SetResult();
        await slow;
        inner.Stored("slow").Should().Be(1);
    }

    private sealed class BatchStore : ICheckpointStore, ICheckpointBatchStore
    {
        private readonly Dictionary<string, long> _data = new();
        private readonly Lock _lock = new();

        public int RoundTrips { get; private set; }
        public int Gets { get; private set; }
        public IReadOnlyList<CheckpointWrite> LastWrites { get; private set; } = [];
        public FencingContext? LastFencing { get; private set; }
        public Func<CheckpointWrite, CheckpointWriteOutcome>? Outcome { get; set; }
        public Func<CheckpointWrite, Task>? Gate { get; set; }

        public void Reset() => (RoundTrips, Gets) = (0, 0);

        public long? Stored(string id)
        {
            lock (_lock) return _data.TryGetValue(id, out var v) ? v : null;
        }

        public void Set(string id, long position)
        {
            lock (_lock) _data[id] = position;
        }

        public Task<IReadOnlyDictionary<string, long?>> GetManyAsync(
            IReadOnlyCollection<string> processorIds, CancellationToken cancellationToken)
        {
            Gets++;
            IReadOnlyDictionary<string, long?> result = processorIds.ToDictionary(id => id, Stored);
            return Task.FromResult(result);
        }

        public async Task<IReadOnlyDictionary<string, CheckpointWriteOutcome>> SaveManyAsync(
            IReadOnlyList<CheckpointWrite> writes, FencingContext? fencing, CancellationToken cancellationToken)
        {
            RoundTrips++;
            LastWrites = writes;
            LastFencing = fencing;
            foreach (var w in writes)
                if (Gate is not null)
                    await Gate(w);

            var outcomes = new Dictionary<string, CheckpointWriteOutcome>();
            foreach (var w in writes)
            {
                var outcome = Outcome?.Invoke(w) ?? new(CheckpointWriteResult.Written);
                if (outcome.Result == CheckpointWriteResult.Written)
                    lock (_lock) _data[w.ProcessorId] = Math.Max(_data.GetValueOrDefault(w.ProcessorId), w.Position);
                outcomes[w.ProcessorId] = outcome;
            }
            return outcomes;
        }

        public Task<long?> GetAsync(string processorId, CancellationToken cancellationToken = default)
        {
            Gets++;
            return Task.FromResult(Stored(processorId));
        }

        public Task SaveAsync(string processorId, long position, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a batch store is written through SaveManyAsync");

        public Task ResetAsync(string processorId, CancellationToken cancellationToken = default)
        {
            lock (_lock) _data.Remove(processorId);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken cancellationToken = default)
        {
            Set(processorId, position);
            return Task.CompletedTask;
        }
    }
}
