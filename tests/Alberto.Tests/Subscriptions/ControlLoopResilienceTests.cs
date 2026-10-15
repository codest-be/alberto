using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// ControlLoop survives transient infrastructure failures on its read path (M8) and does not
/// hot-spin when a full batch holds nothing at or below the head (M9).
/// </summary>
public sealed class ControlLoopResilienceTests
{
    private const string TypeId = "cl-resilience-event";

    [EventType(TypeId)]
    private record ResilienceEvent(string Value) : IEvent;

    private sealed class CountingProcessor(string id) : IEventProcessor
    {
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);
        public string ProcessorId { get; } = id;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { TypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _processed);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedBackend(Func<int, long, IReadOnlyCollection<IEventEnvelope>> reader) : IEventStoreBackend
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
        {
            // Yield so a spinning loop shows up as a high call count rather than blocking StartAsync.
            await Task.Yield();
            return reader(Interlocked.Increment(ref _calls), afterPosition);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query, long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null, long? expectedPosition = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedHead(long upTo) : IEventStoreHeadBackend
    {
        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>(
                Enumerable.Range(1, (int)upTo).Select(i => (long)i).Where(p => p > afterPosition).ToList());
    }

    private static IEventEnvelope MakeEvent(long position) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = position,
        EventType = new EventType(TypeId),
        Tags = [],
        EventData = System.Text.Json.JsonSerializer.Serialize(new ResilienceEvent("x")),
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 10s.");
            await Task.Delay(10, ct);
        }
    }

    private static async Task<ControlLoop> StartAsync(
        IEventProcessor processor, IEventStoreBackend backend, ICheckpointStore checkpoints,
        CancellationTokenSource cts, int maxConcurrency, int batchSize = 3)
    {
        var head = new EventStoreHead(new FixedHead(5));
        await head.RefreshAsync(TestContext.Current.CancellationToken);
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), batchSize,
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = maxConcurrency });
        await loop.StartAsync(cts.Token);
        return loop;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TransientReadFailure_IsRetriedInsteadOfFaulting(int maxConcurrency)
    {
        var backend = new ScriptedBackend((call, after) =>
            call <= 2
                ? throw new TimeoutException("db blip")
                : after == 0 ? [MakeEvent(1), MakeEvent(2)] : []);
        var processor = new CountingProcessor("transient-read");
        using var cts = new CancellationTokenSource();

        var loop = await StartAsync(processor, backend, new InMemoryCheckpointStore(), cts, maxConcurrency);
        await WaitForAsync(() => processor.Processed == 2, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);

        loop.IsFaulted.Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FullBatchBeyondHead_DoesNotHotSpin(int maxConcurrency)
    {
        // Every read returns a full batch of events above the head: nothing visible, no progress.
        var backend = new ScriptedBackend((_, _) => [MakeEvent(100), MakeEvent(101), MakeEvent(102)]);
        using var cts = new CancellationTokenSource();

        var loop = await StartAsync(new CountingProcessor("no-spin"), backend, new InMemoryCheckpointStore(), cts, maxConcurrency);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);

        // 10ms polling interval: a delayed loop reads ~30 times in 300ms, a spinning one thousands.
        backend.Calls.Should().BeLessThan(100);
    }
}
