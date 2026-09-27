using System.Diagnostics.Metrics;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Targeted tests killing specific Stryker mutation survivors in the sequential
/// (<c>MaxConcurrency == 1</c>) path of <see cref="ControlLoop.RunAsync"/>. Each test's summary
/// names the mutant(s) it kills. Style follows <see cref="ProcessorHealthTests"/> and
/// <see cref="ControlLoopTests"/>: <see cref="InMemoryEventStoreBackend"/>/<see cref="EventStoreHead"/>
/// for realistic scenarios, small scripted fakes where a mutant needs a shape the real backend
/// can't produce deterministically (e.g. events beyond the visible head).
/// </summary>
public sealed class ControlLoopSequentialMutationTests
{
    private const string TestEventTypeId = "cl-mutation-test-event";

    [EventType(TestEventTypeId)]
    public record TestEvent(string Value) : IEvent;

    #region Fakes

    /// <summary>A processor whose per-event and batch dispatch both just count.</summary>
    private sealed class CountingProcessor(string processorId) : IBatchableProcessor
    {
        private int _processedCount;
        private int _batchCount;
        public int ProcessedCount => Volatile.Read(ref _processedCount);
        public int BatchCount => Volatile.Read(ref _batchCount);

        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { TestEventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _processedCount);
            return Task.CompletedTask;
        }

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _batchCount);
            Interlocked.Add(ref _processedCount, events.Count);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProcessor(string processorId) : IBatchableProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { TestEventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated fault");

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated fault");
    }

    /// <summary>
    /// An <see cref="IEventStoreBackend"/> whose <see cref="StreamAllAsync"/> is fully scripted
    /// and whose call count/timestamps are observable, so a test can put the head and the events
    /// a batch read returns into any relationship a mutant needs to expose, independent of what
    /// a real backend would ever actually produce together.
    /// </summary>
    private sealed class ScriptedBackend : IEventStoreBackend
    {
        private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public List<TimeSpan> CallTimestamps { get; } = [];
        public Func<long, int?, IReadOnlyCollection<IEventEnvelope>> Reader { get; set; } = (_, _) => [];

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            lock (CallTimestamps) CallTimestamps.Add(_stopwatch.Elapsed);
            return Task.FromResult(Reader(afterPosition, limit));
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query, long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by ControlLoop.");

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null, long? expectedPosition = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by ControlLoop.");

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by ControlLoop.");
    }

    /// <summary>A head backend whose contiguous position set is fixed, for a deterministic head.</summary>
    private sealed class FixedPositionsHeadBackend(IReadOnlyList<long> positions) : IEventStoreHeadBackend
    {
        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>(positions.Where(p => p > afterPosition).ToList());
    }

    /// <summary>Wraps an <see cref="ICheckpointStore"/> and records every SaveAsync invocation, args included.</summary>
    private sealed class SpyCheckpointStore(ICheckpointStore inner) : ICheckpointStore
    {
        public List<(string ProcessorId, long Position)> SaveCalls { get; } = [];

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default) => inner.GetAsync(processorId, ct);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            lock (SaveCalls) SaveCalls.Add((processorId, position));
            return inner.SaveAsync(processorId, position, ct);
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default) => inner.ResetAsync(processorId, ct);

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => inner.RewindAsync(processorId, position, ct);
    }

    private sealed class CapturingLogger : ILogger<ControlLoop>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static IEventEnvelope MakeEvent(long position, string typeId = TestEventTypeId) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = position,
        EventType = new EventType(typeId),
        Tags = [],
        EventData = System.Text.Json.JsonSerializer.Serialize(new TestEvent("x")),
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static EventToPersist Event() => new()
    {
        EventType = new EventType(TestEventTypeId),
        Tags = [],
        EventData = System.Text.Json.JsonSerializer.Serialize(new TestEvent("x")),
    };

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 10s.");
            await Task.Delay(10, ct);
        }
    }

    #endregion

    // Kills: 230 (checkpoint >= head => >), 234 (continue removed), 232 (ReportProgress(0L) removed).
    [Fact]
    public async Task Loop_neither_reads_nor_stalls_heartbeats_once_checkpoint_reaches_head()
    {
        var backend = new ScriptedBackend(); // never scripted to return anything meaningful
        var head = new EventStoreHead(new FixedPositionsHeadBackend([1, 2, 3, 4, 5]));
        await head.RefreshAsync(TestContext.Current.CancellationToken); // head.Current == 5
        var checkpoints = new InMemoryCheckpointStore();
        await checkpoints.SaveAsync("caught-up", 5, TestContext.Current.CancellationToken); // == head already
        var state = new ProcessorHealthState();
        var processor = new CountingProcessor("caught-up");

        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(15), 100, healthState: state);

        using var cts = new CancellationTokenSource();
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => state.Get("caught-up").HasReported, TestContext.Current.CancellationToken);
        await Task.Delay(80, TestContext.Current.CancellationToken); // let a few idle poll cycles pass
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);

        backend.CallCount.Should().Be(0, "checkpoint already at head — the loop must skip forward without reading");
        state.Get("caught-up").Lag.Should().Be(0);
    }

    // Kills: NoCoverage branch at 242-245 ("no events between checkpoint and head — skip forward safely").
    [Fact]
    public async Task Loop_skips_forward_to_head_when_the_read_returns_no_events()
    {
        var backend = new ScriptedBackend { Reader = (_, _) => [] }; // always empty, regardless of head
        var head = new EventStoreHead(new FixedPositionsHeadBackend([1, 2, 3, 4, 5]));
        await head.RefreshAsync(TestContext.Current.CancellationToken); // head.Current == 5
        var checkpoints = new InMemoryCheckpointStore();
        var state = new ProcessorHealthState();
        var processor = new CountingProcessor("skip-forward");

        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(15), 100, healthState: state);

        using var cts = new CancellationTokenSource();
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => checkpoints.GetAsync("skip-forward", TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult() == 5, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);

        var savedCheckpoint = await checkpoints.GetAsync("skip-forward", TestContext.Current.CancellationToken);
        savedCheckpoint.Should().Be(5, "an empty read must still advance the checkpoint to the head");
        state.Get("skip-forward").HasReported.Should().BeTrue();
        state.Get("skip-forward").Lag.Should().Be(0);
        processor.ProcessedCount.Should().Be(0);
    }

    // Kills: 254 (GlobalPosition > head filter removed), 260 (relevantEvents.Count > 0 => >= 0),
    // 266 (visibleEvents.Count > 0 conditional => true/>=0), 270 (newCheckpoint > checkpoint => >=).
    [Fact]
    public async Task Loop_ignores_a_batch_entirely_beyond_the_visible_head()
    {
        // The read returns events past the barrier the real backend would never hand back together
        // with this head — that's the point: it isolates the in-loop filter (line 254) from head
        // computation.
        var backend = new ScriptedBackend
        {
            Reader = (_, _) => [MakeEvent(10), MakeEvent(11)],
        };
        var head = new EventStoreHead(new FixedPositionsHeadBackend([1, 2, 3]));
        await head.RefreshAsync(TestContext.Current.CancellationToken); // head.Current == 3, events are at 10/11
        var innerCheckpoints = new InMemoryCheckpointStore();
        var checkpoints = new SpyCheckpointStore(innerCheckpoints);
        var processor = new CountingProcessor("beyond-head");

        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(15), 100);

        using var cts = new CancellationTokenSource();
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => backend.CallCount >= 3, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);

        loop.IsFaulted.Should().BeFalse("an empty visibleEvents ternary must not index the list (266)");
        processor.ProcessedCount.Should().Be(0, "both events are past head and must be filtered out (254)");
        processor.BatchCount.Should().Be(0, "zero relevant events must not trigger a batch dispatch (260)");
        checkpoints.SaveCalls.Should().BeEmpty("checkpoint must not move when nothing became visible (270)");
    }

    // Kills: 273 (ReportProgress(head - newCheckpoint) removed, or => head + newCheckpoint).
    [Fact]
    public async Task Loop_reports_lag_as_head_minus_new_checkpoint()
    {
        var backend = new ScriptedBackend
        {
            Reader = (after, _) => after == 0 ? [MakeEvent(1), MakeEvent(2), MakeEvent(3)] : [],
        };
        var head = new EventStoreHead(new FixedPositionsHeadBackend([1, 2, 3, 4, 5]));
        await head.RefreshAsync(TestContext.Current.CancellationToken); // head.Current == 5
        var checkpoints = new InMemoryCheckpointStore();
        var state = new ProcessorHealthState();
        var processor = new CountingProcessor("lag-report");

        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(15), 100, healthState: state);

        using var cts = new CancellationTokenSource();
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => processor.ProcessedCount == 3, TestContext.Current.CancellationToken);
        await WaitForAsync(() => state.Get("lag-report").Lag == 2, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);

        // newCheckpoint == 3, head == 5 => lag must be exactly 2 (not 0/removed, not 8/head+newCheckpoint).
        state.Get("lag-report").Lag.Should().Be(2);
    }

    // Kills: 276 (events.Count < _batchSize => <=/negated), 277 (Delay removed).
    [Fact]
    public async Task Loop_only_delays_after_a_partial_batch_not_after_a_full_one()
    {
        const int batchSize = 3;
        var pollingInterval = TimeSpan.FromMilliseconds(300);
        var callIndex = 0;
        var backend = new ScriptedBackend
        {
            Reader = (_, _) =>
            {
                var i = Interlocked.Increment(ref callIndex);
                return i switch
                {
                    1 => [MakeEvent(1), MakeEvent(2), MakeEvent(3)], // full batch — no delay expected next
                    2 => [MakeEvent(4)],                              // partial batch — delay expected next
                    _ => [],
                };
            },
        };
        var head = new EventStoreHead(new FixedPositionsHeadBackend(Enumerable.Range(1, 50).Select(i => (long)i).ToList()));
        await head.RefreshAsync(TestContext.Current.CancellationToken); // head.Current == 50, far beyond available events
        var checkpoints = new InMemoryCheckpointStore();
        var processor = new CountingProcessor("batch-delay");

        var loop = new ControlLoop(processor, head, backend, checkpoints, pollingInterval, batchSize);

        using var cts = new CancellationTokenSource();
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => backend.CallCount >= 3, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);

        var timestamps = backend.CallTimestamps;
        timestamps.Count.Should().BeGreaterThanOrEqualTo(3);
        var gapAfterFullBatch = timestamps[1] - timestamps[0];
        var gapAfterPartialBatch = timestamps[2] - timestamps[1];

        gapAfterFullBatch.Should().BeLessThan(TimeSpan.FromMilliseconds(150),
            "a full batch (events.Count == batchSize) must not delay before the next read");
        gapAfterPartialBatch.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200),
            "a partial batch (events.Count < batchSize) must delay before the next read");
    }

    // Kills: 284 (LogCritical on fault removed).
    [Fact]
    public async Task A_fault_logs_critical()
    {
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var logger = new CapturingLogger();
        var processor = new ThrowingProcessor("fault-logs");
        var loop = new ControlLoop(processor, head, backend, new InMemoryCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100, logger: logger);

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => loop.IsFaulted, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Critical && e.Message.Contains("fault-logs") && e.Message.Contains("faulted"));
    }

    // Kills: 215 (LogInformation "starting" removed), 291 (LogInformation "stopped" removed).
    [Fact]
    public async Task Sequential_loop_logs_starting_and_stopped()
    {
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var logger = new CapturingLogger();
        var processor = new CountingProcessor("start-stop-log");
        var loop = new ControlLoop(processor, head, backend, new InMemoryCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100, logger: logger);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("starting"))
            .Should().Be(1);
        logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("stopped"))
            .Should().Be(1);
    }

    // Kills: 220 (return after RunPipelinedAsync removed) — without it, control falls through into
    // the sequential while loop and its trailing "stopped" log fires a second time.
    [Fact]
    public async Task Pipelined_mode_logs_stopped_exactly_once()
    {
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var logger = new CapturingLogger();
        var processor = new CountingProcessor("pipelined-stop-log");
        var options = new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = 2 };
        var loop = new ControlLoop(processor, head, backend, new InMemoryCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100, executionOptions: options, logger: logger);

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => processor.ProcessedCount > 0, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("stopped"))
            .Should().Be(1, "pipelined mode must return after RunPipelinedAsync, not fall through to the sequential loop's own 'stopped' log");
    }

    // Kills: 75 (_timeProvider = timeProvider ?? TimeProvider.System => TimeProvider.System).
    [Fact]
    public async Task Heartbeat_timestamp_comes_from_the_injected_time_provider()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var state = new ProcessorHealthState();
        var processor = new CountingProcessor("fake-time");
        var loop = new ControlLoop(processor, head, backend, new InMemoryCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100, healthState: state, timeProvider: fakeTime);

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => state.Get("fake-time").HasReported, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        // If the injected FakeTimeProvider were ignored in favor of TimeProvider.System, this
        // would be a real wall-clock timestamp near "now", not the fixed instant below.
        state.Get("fake-time").LastHeartbeatAt.Should().Be(fakeTime.GetUtcNow());
    }

    // Kills: 522 (AlbertoMetrics.RecordProcessorLag removed).
    [Fact]
    public async Task Loop_records_the_processor_lag_metric()
    {
        var processorId = $"lag-metric-{Guid.NewGuid():N}";
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new CountingProcessor(processorId);
        var loop = new ControlLoop(processor, head, backend, new InMemoryCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100);

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => processor.ProcessedCount > 0, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        long? observedLag = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AlbertoMetrics.Name && instrument.Name == "alberto.processor.lag")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "processor" && (string?)tag.Value == processorId)
                    observedLag = measurement;
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        observedLag.Should().NotBeNull(
            "RecordProcessorLag must have been called for this processor id at least once");
    }

    // --- Skipped survivors (documented, not tested) ---
    //
    // 279: "catch (OperationCanceledException ...) { break; }" with `break` removed.
    // IsShutdownCancellation(ex, ct) requires ct.IsCancellationRequested to be true before this
    // catch can even match, so the enclosing `while (!ct.IsCancellationRequested)` is already
    // guaranteed false on the very next loop-condition check regardless of whether `break` ran.
    // Equivalent mutant: no test can observe a difference.
}
