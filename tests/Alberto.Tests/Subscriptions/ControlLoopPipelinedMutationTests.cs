using System.Text.Json;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Targeted regression tests for pipelined-path (<c>MaxConcurrency &gt; 1</c>) Stryker survivors
/// in <c>ControlLoop.RunPipelinedAsync</c> and <c>SaveWatermarkCheckpointAsync</c>. Each test is
/// written to fail under one specific hand-applied mutation and pass against the real code; see
/// the class-level remarks on each test for which survivor(s) it targets.
///
/// <para>
/// Style mirrors <see cref="ControlLoopPipelinedCancellationTests"/> and
/// <see cref="ControlLoopDrainTimeoutTests"/>: no wall-clock races beyond a generous, one-sided
/// timing margin explicitly called out per test, and all synchronisation points are
/// <see cref="TaskCompletionSource"/> gates or polling loops with a 10s deadline.
/// </para>
/// </summary>
public sealed class ControlLoopPipelinedMutationTests
{
    // ── shared event type ──────────────────────────────────────────────────────────

    [EventType("pipelined-mutation-event")]
    private record PipelinedMutationEvent(string Label) : IEvent;

    private static readonly string EventTypeId =
        EventTypeAttribute.GetEventTypeId(typeof(PipelinedMutationEvent));

    private static EventToPersist CreateEvent(string label) =>
        new()
        {
            EventType = new EventType(EventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(new PipelinedMutationEvent(label)),
        };

    private static readonly ProcessorExecutionOptions Pipelined2 =
        new() { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = 2 };

    private static readonly ProcessorExecutionOptions Pipelined3 =
        new() { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = 3 };

    // ── test doubles ──────────────────────────────────────────────────────────────

    /// <summary>Delegates every event to a supplied handler. Same shape as the sibling test files.</summary>
    private sealed class DelegatingProcessor(
        string processorId,
        Func<IEventEnvelope, CancellationToken, Task> handler) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }

        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => handler(@event, ct);
    }

    /// <summary>Wraps an <see cref="IEventStoreBackend"/>, counting <see cref="StreamAllAsync"/> calls.</summary>
    private sealed class CountingBackend(IEventStoreBackend inner) : IEventStoreBackend
    {
        private int _streamAllCount;
        public int StreamAllCount => Volatile.Read(ref _streamAllCount);

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _streamAllCount);
            return inner.StreamAllAsync(afterPosition, limit, cancellationToken);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query, long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
            => inner.StreamAsync(query, afterPosition, limit, cancellationToken);

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null, long? expectedPosition = null,
            CancellationToken cancellationToken = default)
            => inner.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken);

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
            => inner.GetLastPositionAsync(cancellationToken);
    }

    /// <summary>Wraps an <see cref="ICheckpointStore"/>, counting Get/Save calls.</summary>
    private sealed class CountingCheckpointStore(ICheckpointStore inner) : ICheckpointStore
    {
        private int _getCount;
        private int _saveCount;
        public int GetCount => Volatile.Read(ref _getCount);
        public int SaveCount => Volatile.Read(ref _saveCount);

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getCount);
            return inner.GetAsync(processorId, ct);
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _saveCount);
            return inner.SaveAsync(processorId, position, ct);
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => inner.ResetAsync(processorId, ct);

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => inner.RewindAsync(processorId, position, ct);
    }

    /// <summary>
    /// A backend whose <see cref="StreamAllAsync"/> always throws, used to trigger the pipelined
    /// producer's own outer catch (line 382 in ControlLoop.cs) — as opposed to a worker's catch.
    /// </summary>
    private sealed class ThrowingStreamBackend : IEventStoreBackend
    {
        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated producer-side backend failure");

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

    /// <summary>
    /// Reports a fixed contiguous head (via <see cref="IEventStoreHeadBackend"/>) — one further
    /// ahead than <paramref name="inner"/> actually has events for — while delegating and
    /// counting <see cref="StreamAllAsync"/> calls against <paramref name="inner"/>. This is what
    /// lets a partial read (fewer events than the store currently has) be distinguished from a
    /// "caught up to head" read: with a real <see cref="EventStoreHead"/>, the two collapse into
    /// the same next iteration and mask a missing/negated delay (see 374/375 below).
    /// </summary>
    private sealed class FixedHeadCountingBackend(long fixedHead, IEventStoreBackend inner)
        : IEventStoreBackend, IEventStoreHeadBackend
    {
        private readonly Lock _lock = new();
        private readonly List<DateTime> _streamAllCallTimestamps = [];
        private int _positionsCalls;

        public int StreamAllCount { get { lock (_lock) return _streamAllCallTimestamps.Count; } }

        /// <summary>Wall-clock time of each <see cref="StreamAllAsync"/> call, in call order.</summary>
        public IReadOnlyList<DateTime> StreamAllCallTimestamps { get { lock (_lock) return [.. _streamAllCallTimestamps]; } }

        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _positionsCalls) == 1)
                return Task.FromResult<IReadOnlyList<long>>(
                    Enumerable.Range(1, (int)fixedHead).Select(i => (long)i).ToList());

            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
        {
            lock (_lock) _streamAllCallTimestamps.Add(DateTime.UtcNow);
            return inner.StreamAllAsync(afterPosition, limit, cancellationToken);
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

    /// <summary>
    /// Decouples the subscription head from the event stream: reports a fixed contiguous head
    /// (via <see cref="IEventStoreHeadBackend"/>) while <see cref="StreamAllAsync"/> (the
    /// <see cref="IEventStoreBackend"/> half) always returns no events — the "head advanced but
    /// nothing is readable there" scenario that lines 346-350 of ControlLoop.cs exist for.
    /// </summary>
    private sealed class AdvancedHeadEmptyStreamBackend(long fixedHead) : IEventStoreBackend, IEventStoreHeadBackend
    {
        private int _positionsCalls;

        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        {
            // First call (EventStoreHead's warm-up) reports the full contiguous run so the head
            // becomes fixedHead; later calls report nothing further so it stays there.
            if (Interlocked.Increment(ref _positionsCalls) == 1)
                return Task.FromResult<IReadOnlyList<long>>(
                    Enumerable.Range(1, (int)fixedHead).Select(i => (long)i).ToList());

            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query, long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null, long? expectedPosition = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(fixedHead);
    }

    /// <summary>Thread-safe capturing <see cref="ILogger{T}"/>, same shape as the one in
    /// <c>EventStoreHeadStallDetectionTests</c>.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _lock = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_lock) return [.. _entries]; }
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within the deadline.");
            await Task.Delay(10, ct);
        }
    }

    private static async Task WaitForCheckpointAsync(
        ICheckpointStore checkpoints, string processorId, long expected, CancellationToken ct)
    {
        await WaitForAsync(
            () => checkpoints.GetAsync(processorId, ct).GetAwaiter().GetResult() >= expected, ct);
    }

    // ── 303: initialCheckpoint = GetAsync ?? 0L => 0L ──────────────────────────────

    /// <summary>
    /// A pipelined loop must resume from an existing checkpoint, not always from zero. Events at
    /// or below the pre-seeded checkpoint must never be redelivered.
    /// </summary>
    [Fact]
    public async Task Pipelined_ResumesFromExistingCheckpoint_DoesNotReprocessEarlierEvents()
    {
        const string processorId = "resume-from-checkpoint";
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync([CreateEvent("e1")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("e2")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("e3")], cancellationToken: TestContext.Current.CancellationToken);

        // Pre-seed the checkpoint past positions 1 and 2 before the loop ever starts.
        await checkpoints.SaveAsync(processorId, 2, TestContext.Current.CancellationToken);

        var recorded = new List<long>();
        var recordedLock = new Lock();
        var processor = new DelegatingProcessor(processorId, (evt, _) =>
        {
            lock (recordedLock) recorded.Add(evt.GlobalPosition);
            return Task.CompletedTask;
        });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 50, executionOptions: Pipelined2);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForAsync(() => { lock (recordedLock) return recorded.Contains(3); },
            TestContext.Current.CancellationToken);

        // Give any spurious redelivery of 1/2 a moment to show up before asserting.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        lock (recordedLock)
        {
            recorded.Should().Contain(3);
            recorded.Should().NotContain(1, "position 1 is before the pre-seeded checkpoint");
            recorded.Should().NotContain(2, "position 2 is before the pre-seeded checkpoint");
        }
    }

    // ── 330: readPosition >= head => > ─────────────────────────────────────────────

    /// <summary>
    /// Once the pipelined producer has caught up to the head, it must stop calling
    /// <see cref="IEventStoreBackend.StreamAllAsync"/> until the head advances again. Since the
    /// watermark's read position never exceeds the head, weakening <c>&gt;=</c> to <c>&gt;</c>
    /// makes the "caught up" shortcut never fire, so the loop would call StreamAllAsync on every
    /// poll cycle forever instead of idling.
    /// </summary>
    [Fact]
    public async Task Pipelined_StopsReadingBackend_OnceCaughtUp()
    {
        const string processorId = "catch-up-stops-reading";
        var backend = new InMemoryEventStoreBackend();
        var countingBackend = new CountingBackend(backend);
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync([CreateEvent("only")], cancellationToken: TestContext.Current.CancellationToken);

        var processor = new DelegatingProcessor(processorId, (_, _) => Task.CompletedTask);
        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, countingBackend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(80), batchSize: 50, executionOptions: Pipelined2);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForCheckpointAsync(checkpoints, processorId, 1, TestContext.Current.CancellationToken);
        var countAfterCatchUp = countingBackend.StreamAllCount;

        // Several poll intervals' worth of idle time — the real code makes no further calls here.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        countingBackend.StreamAllCount.Should().Be(countAfterCatchUp,
            "once caught up, the loop must idle rather than re-read the backend every poll cycle");
    }

    // ── 337: ReportProgress(head - SafeCheckpoint) => head + ... (also exercises 372, same expr) ──

    /// <summary>
    /// The reported lag is <c>head - SafeCheckpoint</c>, not their sum. Blocks one worker to keep
    /// the watermark's safe checkpoint behind the head while the other two positions complete, so
    /// the gap is stable and non-zero (0 would not distinguish + from -).
    /// </summary>
    [Fact]
    public async Task Pipelined_Lag_IsHeadMinusSafeCheckpoint_WhileAWorkerIsBlocked()
    {
        const string processorId = "lag-arithmetic";
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var healthState = new ProcessorHealthState();

        await backend.AppendAsync([CreateEvent("fast-1")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("stuck-2")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("fast-3")], cancellationToken: TestContext.Current.CancellationToken);

        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(processorId, async (evt, _) =>
        {
            if (evt.GlobalPosition == 2)
            {
                gate2.TrySetResult();
                await release2.Task;
                return;
            }
        });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 50,
            executionOptions: Pipelined3, healthState: healthState);

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);
            await loop.StartAsync(cts.Token);

            await gate2.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // head = 3, SafeCheckpoint pins at 1 (position 2 is the sole in-flight position) once
            // positions 1 and 3 finish, so head - SafeCheckpoint stabilises at 2. head + SafeCheckpoint
            // would instead read 4 — the two are far enough apart that there is no ambiguity.
            await WaitForAsync(() => healthState.Get(processorId).Lag == 2,
                TestContext.Current.CancellationToken);

            healthState.Get(processorId).Lag.Should().Be(2);

            await cts.CancelAsync();
            release2.TrySetResult();
            await loop.StopAsync(CancellationToken.None);
            await head.StopAsync(CancellationToken.None);
        }
        finally
        {
            release2.TrySetResult();
        }
    }

    // ── 338 (delay removed) + 532/533 (redundant SaveAsync on idle) ────────────────

    /// <summary>
    /// Once idle at head, the loop must (a) not busy-spin the "caught up" branch — killing the
    /// removed <c>Task.Delay</c> at line 338 — and (b) not call <c>SaveAsync</c> again once the
    /// checkpoint store already agrees with the safe checkpoint — killing the <c>?? 0L =&gt; 0L</c>
    /// and <c>&gt; =&gt; &gt;=</c> mutants on lines 532/533 of <c>SaveWatermarkCheckpointAsync</c>,
    /// both of which force a redundant save every idle cycle.
    /// </summary>
    [Fact]
    public async Task Pipelined_IdleAfterCatchUp_DoesNotHammerTheCheckpointStore()
    {
        const string processorId = "idle-hammer";
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new CountingCheckpointStore(new InMemoryCheckpointStore());

        await backend.AppendAsync([CreateEvent("only")], cancellationToken: TestContext.Current.CancellationToken);

        var processor = new DelegatingProcessor(processorId, (_, _) => Task.CompletedTask);
        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(150), batchSize: 50, executionOptions: Pipelined2);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForCheckpointAsync(checkpoints, processorId, 1, TestContext.Current.CancellationToken);

        // Phase 1 (kills the removed delay at line 338): a short window, far below the polling
        // interval. A busy-spinning loop would rack up thousands of GetAsync calls here.
        var getCountBeforeShortWindow = checkpoints.GetCount;
        await Task.Delay(40, TestContext.Current.CancellationToken);
        var getCountAfterShortWindow = checkpoints.GetCount;
        (getCountAfterShortWindow - getCountBeforeShortWindow).Should().BeLessThanOrEqualTo(3,
            "removing the caught-up delay would busy-spin, calling GetAsync far more than once " +
            "in a window well under one polling interval");

        // Phase 2 (kills lines 532/533): span several more polling intervals with nothing new to
        // checkpoint. A correctly-guarded SaveWatermarkCheckpointAsync never calls SaveAsync again
        // once the store already holds the safe checkpoint.
        var saveCountBeforeLongWindow = checkpoints.SaveCount;
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var saveCountAfterLongWindow = checkpoints.SaveCount;

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        saveCountAfterLongWindow.Should().Be(saveCountBeforeLongWindow,
            "SaveAsync must not be called again once the checkpoint store already agrees with the " +
            "safe checkpoint — a stale 'current' or a >= guard would call it every idle cycle");
    }

    // ── 346-350: NoCoverage — head advanced but the read returned nothing ─────────

    /// <summary>
    /// When the head has moved ahead of the read position but a read at that position comes back
    /// empty, the loop must still advance the watermark's read position to the head and flush the
    /// checkpoint — otherwise it can never make progress and the checkpoint is stuck forever.
    /// </summary>
    [Fact]
    public async Task Pipelined_HeadAdvancedButReadIsEmpty_StillAdvancesCheckpointToHead()
    {
        const string processorId = "empty-read-advances-to-head";
        var stub = new AdvancedHeadEmptyStreamBackend(fixedHead: 5);
        var checkpoints = new InMemoryCheckpointStore();
        var processor = new DelegatingProcessor(processorId, (_, _) => Task.CompletedTask);

        var head = new EventStoreHead(stub, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, stub, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(20), batchSize: 50, executionOptions: Pipelined2);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForCheckpointAsync(checkpoints, processorId, 5, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        (await checkpoints.GetAsync(processorId, TestContext.Current.CancellationToken)).Should().Be(5);
    }

    // ── 374 (negated) + 375 (delay removed): partial-batch delay ───────────────────

    /// <summary>
    /// After a partial read (fewer events than the batch size — meaning nothing more is currently
    /// available), the loop must wait a full polling interval before reading again. Negating the
    /// guard or removing the delay both cause an extra, near-immediate re-read.
    /// <para>
    /// A real <see cref="EventStoreHead"/> would track the backend exactly, so "caught up to
    /// head" and "read fewer than a full batch" collapse into the same next iteration and the
    /// idle branch's own delay (330/338) masks a broken 374/375 — the idle branch delays either
    /// way. <see cref="FixedHeadCountingBackend"/> reports a head three positions ahead of what
    /// the backend actually holds, so after the partial read <c>readPosition (2) &lt; head (5)</c>
    /// and the *only* delay standing between this call and the next <c>StreamAllAsync</c> is
    /// 374/375 itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pipelined_PartialBatch_DelaysBeforeReadingAgain()
    {
        const string processorId = "partial-batch-delay";
        var backend = new InMemoryEventStoreBackend();
        var fixedHeadBackend = new FixedHeadCountingBackend(fixedHead: 5, backend);
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync(
            [CreateEvent("a"), CreateEvent("b")], cancellationToken: TestContext.Current.CancellationToken);

        var processor = new DelegatingProcessor(processorId, (_, _) => Task.CompletedTask);
        var head = new EventStoreHead(fixedHeadBackend, refreshInterval: TimeSpan.FromMilliseconds(10));
        // batchSize (3) > events actually stored (2), so the first read is already partial —
        // but head (5) is still ahead of readPosition (2), so the next iteration re-reads
        // instead of taking the "caught up" idle branch.
        var loop = new ControlLoop(processor, head, fixedHeadBackend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(500), batchSize: 3, executionOptions: Pipelined2);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        // Wait for the second StreamAllAsync call (the re-read after the partial batch) rather
        // than for the checkpoint: under the mutation the watermark jumps straight from 2 to head
        // (the re-read comes back empty), too fast for a checkpoint-based poll to reliably land on
        // the intermediate value. Comparing the two calls' own timestamps sidesteps that race
        // entirely — it needs no assumption about what happens after the second call.
        await WaitForAsync(() => fixedHeadBackend.StreamAllCount >= 2, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        var timestamps = fixedHeadBackend.StreamAllCallTimestamps;
        var gap = timestamps[1] - timestamps[0];

        gap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300),
            "a partial read must delay a full polling interval before reading again, even though " +
            "the head has not been caught up to yet");
    }

    // ── 384 (ReportFailure removed) + 426 (ReportFaulted removed) ──────────────────

    /// <summary>
    /// A failure in the pipelined producer itself (not a worker) must fault the loop, report the
    /// fault into <see cref="ProcessorHealthState"/>, and log Critical. All three only happen
    /// inside the <c>if (pipelineFailure is not null)</c> block, which only runs if
    /// <c>ReportFailure</c> actually recorded the exception — so asserting all three together
    /// kills both the removed <c>ReportFailure(ex)</c> call and the removed <c>ReportFaulted()</c>
    /// call in one scenario.
    /// </summary>
    [Fact]
    public async Task Pipelined_ProducerFailure_FaultsLoop_ReportsHealth_AndLogsCritical()
    {
        const string processorId = "producer-failure";
        var eventBackend = new InMemoryEventStoreBackend();
        await eventBackend.AppendAsync([CreateEvent("triggers-head")],
            cancellationToken: TestContext.Current.CancellationToken);

        var throwingBackend = new ThrowingStreamBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var healthState = new ProcessorHealthState();
        var logger = new CapturingLogger<ControlLoop>();
        var processor = new DelegatingProcessor(processorId, (_, _) => Task.CompletedTask);

        // The head is driven by a real backend so it advances past 0 and the loop actually
        // attempts a read (which is where throwingBackend.StreamAllAsync blows up).
        var head = new EventStoreHead(eventBackend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, throwingBackend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 50,
            executionOptions: Pipelined2, logger: logger, healthState: healthState);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForAsync(() => loop.IsFaulted, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        loop.IsFaulted.Should().BeTrue();
        healthState.Get(processorId).IsFaulted.Should().BeTrue();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Critical && e.Message.Contains(processorId));
    }

    // ── 391/406: happy-path "drained" flag must not trigger the timeout branch ─────

    /// <summary>
    /// A normal shutdown where every worker finishes well within the drain timeout must not log
    /// the "abandoned worker(s)" warning. Flipping the <c>drained</c> flag's initial value, or the
    /// <c>if (drained)</c> check, would route this happy path into the timeout branch anyway.
    /// </summary>
    [Fact]
    public async Task Pipelined_NormalShutdown_DoesNotLogTheAbandonedWorkerWarning()
    {
        const string processorId = "normal-shutdown-no-warning";
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var logger = new CapturingLogger<ControlLoop>();

        await backend.AppendAsync([CreateEvent("fast-1")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("fast-2")], cancellationToken: TestContext.Current.CancellationToken);

        var processor = new DelegatingProcessor(processorId, (_, _) => Task.CompletedTask);
        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 50,
            executionOptions: Pipelined2, logger: logger, drainTimeout: TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForCheckpointAsync(checkpoints, processorId, 2, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await head.StopAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("abandoned"),
            "no worker was stuck, so the drain-timeout warning must not fire");
    }

    // ── 399/412/416: genuine timeout must warn, with the correct abandoned-worker count ──

    /// <summary>
    /// A genuinely stuck worker must log the drain-timeout warning, with the correct count of
    /// abandoned workers baked into the message (exactly 1 of the 3 here) — killing the removed
    /// <c>drained = false</c> assignment, the removed <c>LogWarning</c> call, and the
    /// <c>NoCoverage</c> <c>workers.Count(w =&gt; !w.IsCompleted)</c> expression all at once.
    /// </summary>
    [Fact]
    public async Task Pipelined_StuckWorker_LogsAbandonedWorkerWarning_WithCorrectCount()
    {
        const string processorId = "stuck-worker-warning";
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var logger = new CapturingLogger<ControlLoop>();

        await backend.AppendAsync([CreateEvent("fast-1")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("stuck-2")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("fast-3")], cancellationToken: TestContext.Current.CancellationToken);

        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(processorId, async (evt, _) =>
        {
            if (evt.GlobalPosition != 2) return;
            gate2.TrySetResult();
            await release2.Task; // ignores cancellation entirely — the "stuck handler" scenario
        });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 50,
            executionOptions: Pipelined3, logger: logger, drainTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);
            await loop.StartAsync(cts.Token);

            await gate2.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Cancel directly rather than via loop.StopAsync: StopAsync's own outer wait shares
            // the same drainTimeout and would otherwise race the pipeline's internal drain wait
            // that actually produces the log line under test.
            await cts.CancelAsync();

            await WaitForAsync(() => logger.Entries.Any(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("abandoned 1 worker(s)")),
                TestContext.Current.CancellationToken);

            logger.Entries.Should().Contain(e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("abandoned 1 worker(s)"),
                "exactly one of the three workers (position 2's) was stuck");

            await head.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            release2.TrySetResult();
        }
    }
}
