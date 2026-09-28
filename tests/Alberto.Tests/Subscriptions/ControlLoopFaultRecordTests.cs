using System.Text.Json;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// ControlLoop's durable fault record (issue #73): when a processor faults, the failure is
/// persisted next to its checkpoint via <see cref="IProcessorFaultStore"/>; a healthy run
/// clears it once, on its first successful checkpoint save.
/// </summary>
public sealed class ControlLoopFaultRecordTests
{
    [EventType("fault-record-event")]
    private record FaultRecordEvent(string Label) : IEvent;

    private static readonly string EventTypeId =
        EventTypeAttribute.GetEventTypeId(typeof(FaultRecordEvent));

    private static EventToPersist CreateEvent(string label) =>
        new()
        {
            EventType = new EventType(EventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(new FaultRecordEvent(label)),
        };

    private static readonly ProcessorExecutionOptions PerEvent =
        new() { BatchingMode = ProcessorBatchingMode.Disabled };

    private static readonly ProcessorExecutionOptions Pipelined2 =
        new() { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = 2 };

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(15, ct);
        }
        Assert.Fail("Condition not met within 10s");
    }

    // ── test doubles ──────────────────────────────────────────────────────────────

    private sealed class FaultingProcessor(string processorId) : IBatchableProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated fault");

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated fault");
    }

    private sealed class HealthyProcessor(string processorId) : IBatchableProcessor
    {
        private int _processedCount;
        public int ProcessedCount => Volatile.Read(ref _processedCount);

        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _processedCount);
            return Task.CompletedTask;
        }

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
        {
            Interlocked.Add(ref _processedCount, events.Count);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Wraps <see cref="InMemoryCheckpointStore"/>, counting fault-store calls and optionally
    /// replacing the record write with a custom behaviour (throw, hang).
    /// </summary>
    private sealed class InstrumentedFaultStore(InMemoryCheckpointStore inner)
        : ICheckpointStore, IProcessorFaultStore
    {
        private readonly IProcessorFaultStore _innerFaults = inner;
        private int _recordCount;
        private int _clearCount;
        private int _saveCount;

        public int RecordCount => Volatile.Read(ref _recordCount);
        public int ClearCount => Volatile.Read(ref _clearCount);
        public int SaveCount => Volatile.Read(ref _saveCount);

        /// <summary>When set, replaces the inner record write entirely.</summary>
        public Func<ProcessorFaultRecord, CancellationToken, Task>? RecordBehavior { get; set; }

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
            => inner.GetAsync(processorId, ct);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _saveCount);
            return inner.SaveAsync(processorId, position, ct);
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => inner.ResetAsync(processorId, ct);

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => inner.RewindAsync(processorId, position, ct);

        public Task RecordFaultAsync(string processorId, ProcessorFaultRecord fault, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _recordCount);
            return RecordBehavior is { } behavior
                ? behavior(fault, ct)
                : _innerFaults.RecordFaultAsync(processorId, fault, ct);
        }

        public Task ClearFaultAsync(string processorId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _clearCount);
            return _innerFaults.ClearFaultAsync(processorId, ct);
        }

        public Task<ProcessorFaultRecord?> GetFaultAsync(string processorId, CancellationToken ct = default)
            => _innerFaults.GetFaultAsync(processorId, ct);
    }

    /// <summary>Producer-side failure: streaming from the backend always throws.</summary>
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

    // ── harness ───────────────────────────────────────────────────────────────────

    private static async Task RunUntilFaultedAsync(
        IEventProcessor processor,
        IEventStoreBackend loopBackend,
        IEventStoreHeadBackend headBackend,
        ICheckpointStore checkpoints,
        ProcessorExecutionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var head = new EventStoreHead(headBackend, TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(processor, head, loopBackend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100,
            executionOptions: options, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForAsync(() => loop.IsFaulted, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.True(loop.IsFaulted);
    }

    // ── recording ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SequentialPerEventFault_RecordsTheFaultingEnvelope()
    {
        var now = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        await backend.AppendAsync([CreateEvent("boom")], cancellationToken: TestContext.Current.CancellationToken);

        await RunUntilFaultedAsync(new FaultingProcessor("seq-per-event"),
            backend, backend, checkpoints, PerEvent, time);

        var fault = await ((IProcessorFaultStore)checkpoints)
            .GetFaultAsync("seq-per-event", TestContext.Current.CancellationToken);
        Assert.NotNull(fault);
        Assert.Equal(now, fault.FaultedAt);
        Assert.Equal("Simulated fault", fault.Message);
        Assert.NotNull(fault.StackTrace);
        Assert.Equal(1L, fault.Position);
        Assert.Equal(EventTypeId, fault.EventType);
        Assert.Null(fault.TenantId);
    }

    [Fact]
    public async Task SequentialBatchFault_RecordsNullEventContext()
    {
        // Batch dispatch cannot know which event in the batch threw.
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        await backend.AppendAsync([CreateEvent("boom")], cancellationToken: TestContext.Current.CancellationToken);

        await RunUntilFaultedAsync(new FaultingProcessor("seq-batch"),
            backend, backend, checkpoints);

        var fault = await ((IProcessorFaultStore)checkpoints)
            .GetFaultAsync("seq-batch", TestContext.Current.CancellationToken);
        Assert.NotNull(fault);
        Assert.Equal("Simulated fault", fault.Message);
        Assert.Null(fault.Position);
        Assert.Null(fault.EventType);
    }

    [Fact]
    public async Task PipelinedWorkerFault_RecordsTheFaultingEnvelope()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        await backend.AppendAsync([CreateEvent("boom")], cancellationToken: TestContext.Current.CancellationToken);

        await RunUntilFaultedAsync(new FaultingProcessor("pipelined-worker"),
            backend, backend, checkpoints, Pipelined2);

        var fault = await ((IProcessorFaultStore)checkpoints)
            .GetFaultAsync("pipelined-worker", TestContext.Current.CancellationToken);
        Assert.NotNull(fault);
        Assert.Equal("Simulated fault", fault.Message);
        Assert.Equal(1L, fault.Position);
        Assert.Equal(EventTypeId, fault.EventType);
    }

    [Fact]
    public async Task PipelinedProducerFault_RecordsNullEventContext()
    {
        // The head is driven by a real backend so the producer actually attempts the read
        // that blows up; the failure happened between events, so there is no envelope.
        var headBackend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        await headBackend.AppendAsync([CreateEvent("advances-head")], cancellationToken: TestContext.Current.CancellationToken);

        await RunUntilFaultedAsync(new HealthyProcessor("pipelined-producer"),
            new ThrowingStreamBackend(), headBackend, checkpoints, Pipelined2);

        var fault = await ((IProcessorFaultStore)checkpoints)
            .GetFaultAsync("pipelined-producer", TestContext.Current.CancellationToken);
        Assert.NotNull(fault);
        Assert.Equal("Simulated producer-side backend failure", fault.Message);
        Assert.Null(fault.Position);
        Assert.Null(fault.EventType);
    }

    // ── clearing ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthyRun_ClearsAPreviousRunsFault()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var faults = (IProcessorFaultStore)checkpoints;
        await faults.RecordFaultAsync("recovering",
            new ProcessorFaultRecord(DateTimeOffset.UnixEpoch, "old fault", null, 1, EventTypeId, null),
            TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("fine")], cancellationToken: TestContext.Current.CancellationToken);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(10));
        var processor = new HealthyProcessor("recovering");
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(
            () => checkpoints.GetAsync("recovering", CancellationToken.None).GetAwaiter().GetResult() == 1,
            TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.False(loop.IsFaulted);
        Assert.Null(await faults.GetFaultAsync("recovering", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FaultIsCleared_OnlyOnce_PerRun()
    {
        var backend = new InMemoryEventStoreBackend();
        var store = new InstrumentedFaultStore(new InMemoryCheckpointStore());
        await backend.AppendAsync([CreateEvent("one")], cancellationToken: TestContext.Current.CancellationToken);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(10));
        var processor = new HealthyProcessor("clear-once");
        var loop = new ControlLoop(processor, head, backend, store,
            TimeSpan.FromMilliseconds(10), 100);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForAsync(() => store.SaveCount >= 1, TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("two")], cancellationToken: TestContext.Current.CancellationToken);
        await WaitForAsync(() => store.SaveCount >= 2, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.True(store.SaveCount >= 2);
        Assert.Equal(1, store.ClearCount);
        Assert.Equal(0, store.RecordCount);
    }

    // ── record-write resilience ───────────────────────────────────────────────────

    [Fact]
    public async Task FaultRecordWriteFailure_IsSwallowed()
    {
        // The store being written to may be the very thing that failed — a throwing fault
        // write must not mask the original fault or crash the loop's shutdown path.
        var backend = new InMemoryEventStoreBackend();
        var store = new InstrumentedFaultStore(new InMemoryCheckpointStore())
        {
            RecordBehavior = (_, _) => throw new TimeoutException("fault store down"),
        };
        await backend.AppendAsync([CreateEvent("boom")], cancellationToken: TestContext.Current.CancellationToken);

        await RunUntilFaultedAsync(new FaultingProcessor("record-throws"),
            backend, backend, store);

        Assert.Equal(1, store.RecordCount);
        Assert.Null(await store.GetFaultAsync("record-throws", TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync("record-throws", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HangingFaultWrite_IsCancelledAfterTheWriteTimeout()
    {
        // The write is bounded by a 5s budget on the loop's own TimeProvider: advancing fake
        // time past it must cancel the hang, so a dead store cannot stall shutdown forever.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var recordStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recordFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var backend = new InMemoryEventStoreBackend();
        var store = new InstrumentedFaultStore(new InMemoryCheckpointStore())
        {
            RecordBehavior = async (_, ct) =>
            {
                recordStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    recordFinished.TrySetResult();
                }
            },
        };
        await backend.AppendAsync([CreateEvent("boom")], cancellationToken: TestContext.Current.CancellationToken);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(new FaultingProcessor("record-hangs"), head, backend, store,
            TimeSpan.FromMilliseconds(10), 100, timeProvider: time);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await recordStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(recordFinished.Task.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(5));
        await recordFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.True(loop.IsFaulted);
    }
}
