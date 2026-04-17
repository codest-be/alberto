using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Unit tests for ControlLoop and EventStoreHead.
/// Uses InMemoryEventStoreBackend for isolation.
/// </summary>
public sealed class ControlLoopTests
{
    #region Test Events

    [EventType("test-event-a")]
    public record TestEventA(string Value) : IEvent;

    [EventType("test-event-b")]
    public record TestEventB(int Number) : IEvent;

    #endregion

    #region Test Processor

    private class TestProcessor(string processorId, IReadOnlySet<string> handledTypes) : IBatchableProcessor
    {
        public List<IEventEnvelope> ProcessedEvents { get; } = [];

        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = handledTypes;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            ProcessedEvents.Add(@event);
            return Task.CompletedTask;
        }

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
        {
            ProcessedEvents.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private class FaultingProcessor(string processorId, IReadOnlySet<string> handledTypes) : IBatchableProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = handledTypes;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated fault");

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated fault");
    }

    private class NonBatchableProcessor(string processorId, IReadOnlySet<string> handledTypes) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = handledTypes;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private class BatchProcessor(string processorId, IReadOnlySet<string> handledTypes) : IBatchableProcessor
    {
        public List<IEventEnvelope> ProcessedEvents { get; } = [];
        public List<IReadOnlyList<IEventEnvelope>> ProcessedBatches { get; } = [];

        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = handledTypes;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            ProcessedEvents.Add(@event);
            return Task.CompletedTask;
        }

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
        {
            ProcessedBatches.Add(events.ToList());
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingMiddleware
    {
        public List<long> SeenPositions { get; } = [];

        public ConsumeMiddleware ToMiddleware() => async (context, next) =>
        {
            SeenPositions.Add(context.Envelope.GlobalPosition);
            await next();
        };
    }

    private sealed class TrackingBatchMiddleware
    {
        public List<long[]> SeenBatches { get; } = [];

        public BatchConsumeMiddleware ToMiddleware() => async (context, next) =>
        {
            SeenBatches.Add(context.Envelopes.Select(e => e.GlobalPosition).ToArray());
            await next();
        };
    }

    #endregion

    #region EventStoreHead.FindContiguousHead — pure static tests

    [Fact]
    public void FindContiguousHead_EmptyPositions_ReturnsAfterPosition()
    {
        var result = EventStoreHead.FindContiguousHead(5, []);
        Assert.Equal(5, result);
    }

    [Fact]
    public void FindContiguousHead_AllContiguous_ReturnsLast()
    {
        var result = EventStoreHead.FindContiguousHead(5, [6, 7, 8, 9]);
        Assert.Equal(9, result);
    }

    [Fact]
    public void FindContiguousHead_GapAtTail_StopsBeforeGap()
    {
        // Gap at end of window could be in-flight transaction — stop before it
        var result = EventStoreHead.FindContiguousHead(5, [6, 7, 8, 10]);
        Assert.Equal(8, result);
    }

    [Fact]
    public void FindContiguousHead_GapInMiddle_SkipsOver()
    {
        // Gap followed by more positions — permanent gap, safe to skip
        var result = EventStoreHead.FindContiguousHead(5, [6, 7, 8, 10, 11]);
        Assert.Equal(11, result);
    }

    [Fact]
    public void FindContiguousHead_GapAtStart_WithMorePositions_SkipsOver()
    {
        // Gap at start but more positions follow — permanent gap
        var result = EventStoreHead.FindContiguousHead(5, [7, 8, 9]);
        Assert.Equal(9, result);
    }

    [Fact]
    public void FindContiguousHead_SingleContiguous_ReturnsIt()
    {
        var result = EventStoreHead.FindContiguousHead(5, [6]);
        Assert.Equal(6, result);
    }

    [Fact]
    public void FindContiguousHead_SingleGap_ReturnsAfterPosition()
    {
        // Only one position after a gap, no proof it's permanent — stop
        var result = EventStoreHead.FindContiguousHead(5, [7]);
        Assert.Equal(5, result);
    }

    [Fact]
    public void FindContiguousHead_MultipleGaps_SkipsAllButTail()
    {
        // Multiple permanent gaps, then contiguous at end
        var result = EventStoreHead.FindContiguousHead(0, [1, 2, 5, 6, 9, 10, 11]);
        Assert.Equal(11, result);
    }

    [Fact]
    public void FindContiguousHead_MultipleGaps_StopsAtTailGap()
    {
        // Multiple permanent gaps, then a tail gap
        var result = EventStoreHead.FindContiguousHead(0, [1, 2, 5, 6, 9, 10, 12]);
        Assert.Equal(10, result);
    }

    #endregion

    #region EventStoreHead — refresh tests

    [Fact]
    public async Task EventStoreHead_AdvancesAsEventsAppend()
    {
        var backend = new InMemoryEventStoreBackend();
        using var cts = new CancellationTokenSource();

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        await head.StartAsync(cts.Token);

        Assert.Equal(0, head.Current);

        await backend.Append([CreateEvent(new TestEventA("first"))]);
        await backend.Append([CreateEvent(new TestEventA("second"))]);

        // Wait for refresh to catch up
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await head.StopAsync(CancellationToken.None);

        Assert.Equal(2, head.Current);
    }

    #endregion

    #region ControlLoop — processing tests

    [Fact]
    public async Task ControlLoop_ProcessesExistingEvents()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.Append([CreateEvent(new TestEventA("hello"))]);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new TestProcessor("test", new HashSet<string> { "test-event-a" });
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.Single(processor.ProcessedEvents);
    }

    [Fact]
    public async Task ControlLoop_AdvancesCheckpoint()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.Append([CreateEvent(new TestEventA("first"))]);
        await backend.Append([CreateEvent(new TestEventA("second"))]);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new TestProcessor("test", new HashSet<string> { "test-event-a" });
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        var checkpoint = await checkpoints.GetAsync("test");
        Assert.Equal(2, checkpoint);
    }

    [Fact]
    public async Task ControlLoop_OnFault_StopsAndPreservesCheckpoint()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        // Save an existing checkpoint so we know what it was before the fault
        await checkpoints.SaveAsync("faulting", 0);

        await backend.Append([CreateEvent(new TestEventA("trigger"))]);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new FaultingProcessor("faulting", new HashSet<string> { "test-event-a" });
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        // Give the loop time to fault
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        // Loop should be faulted and checkpoint NOT advanced
        Assert.True(loop.IsFaulted);
        var checkpoint = await checkpoints.GetAsync("faulting");
        Assert.Equal(0, checkpoint);
    }

    [Fact]
    public async Task ControlLoop_CancellationIsClean()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new TestProcessor("test", new HashSet<string> { "test-event-a" });
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(50), 100);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await cts.CancelAsync();

        // Should complete without throwing
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.False(loop.IsFaulted);
    }

    [Fact]
    public async Task ControlLoop_OnlyProcessesHandledEventTypes()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.Append([CreateEvent(new TestEventA("a"))]);
        await backend.Append([CreateEvent(new TestEventB(1))]);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processorA = new TestProcessor("proc-a", new HashSet<string> { "test-event-a" });
        var processorB = new TestProcessor("proc-b", new HashSet<string> { "test-event-b" });

        var loopA = new ControlLoop(processorA, head, backend, checkpoints, TimeSpan.FromMilliseconds(10), 100);
        var loopB = new ControlLoop(processorB, head, backend, checkpoints, TimeSpan.FromMilliseconds(10), 100);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loopA.StartAsync(cts.Token);
        await loopB.StartAsync(cts.Token);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loopA.StopAsync(CancellationToken.None);
        await loopB.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.Single(processorA.ProcessedEvents);
        Assert.Equal("test-event-a", processorA.ProcessedEvents[0].EventType.Id);

        Assert.Single(processorB.ProcessedEvents);
        Assert.Equal("test-event-b", processorB.ProcessedEvents[0].EventType.Id);
    }

    [Fact]
    public async Task ControlLoop_UsesBatchProcessor_WhenBatchingIsRequired()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.Append([CreateEvent(new TestEventA("a"))]);
        await backend.Append([CreateEvent(new TestEventB(1))]);
        await backend.Append([CreateEvent(new TestEventA("b"))]);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new BatchProcessor("batch-proc", new HashSet<string> { "test-event-a" });
        var loop = new ControlLoop(
            processor,
            head,
            backend,
            checkpoints,
            TimeSpan.FromMilliseconds(10),
            100,
            executionOptions: new ProcessorExecutionOptions(ProcessorBatchingMode.Required));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        var batch = Assert.Single(processor.ProcessedBatches);
        Assert.Equal([1L, 3L], batch.Select(e => e.GlobalPosition).ToArray());
        Assert.Empty(processor.ProcessedEvents);

        var checkpoint = await checkpoints.GetAsync("batch-proc");
        Assert.Equal(3, checkpoint);
    }

    [Fact]
    public void ControlLoop_RequireBatching_ThrowsForNonBatchableProcessor()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new NonBatchableProcessor("non-batch", new HashSet<string> { "test-event-a" });

        var exception = Assert.Throws<InvalidOperationException>(() => new ControlLoop(
            processor,
            head,
            backend,
            checkpoints,
            TimeSpan.FromMilliseconds(10),
            100,
            executionOptions: new ProcessorExecutionOptions(ProcessorBatchingMode.Required)));

        Assert.Contains("requires batching", exception.Message);
    }

    [Fact]
    public void ControlLoop_RequireBatching_ThrowsWhenMiddlewareIsConfigured()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new BatchProcessor("batch-proc", new HashSet<string> { "test-event-a" });
        var middleware = new TrackingMiddleware();

        var exception = Assert.Throws<InvalidOperationException>(() => new ControlLoop(
            processor,
            head,
            backend,
            checkpoints,
            TimeSpan.FromMilliseconds(10),
            100,
            middlewares: [middleware.ToMiddleware()],
            hasUnpairedPerEventMiddlewares: true,
            executionOptions: new ProcessorExecutionOptions(ProcessorBatchingMode.Required)));

        Assert.Contains("not all configured per-event middleware has a batch equivalent", exception.Message);
    }

    [Fact]
    public async Task ControlLoop_BatchIfSupported_FallsBackToPerEventWhenMiddlewareIsConfigured()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var middleware = new TrackingMiddleware();

        await backend.Append([CreateEvent(new TestEventA("a"))]);
        await backend.Append([CreateEvent(new TestEventA("b"))]);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new BatchProcessor("batch-proc", new HashSet<string> { "test-event-a" });
        var loop = new ControlLoop(
            processor,
            head,
            backend,
            checkpoints,
            TimeSpan.FromMilliseconds(10),
            100,
            middlewares: [middleware.ToMiddleware()],
            hasUnpairedPerEventMiddlewares: true,
            executionOptions: new ProcessorExecutionOptions(ProcessorBatchingMode.IfSupported));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.Equal([1L, 2L], middleware.SeenPositions);
        Assert.Equal([1L, 2L], processor.ProcessedEvents.Select(e => e.GlobalPosition).ToArray());
        Assert.Empty(processor.ProcessedBatches);
    }

    [Fact]
    public async Task ControlLoop_UsesBatchPath_WhenBatchMiddlewareIsConfigured()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var batchMiddleware = new TrackingBatchMiddleware();

        await backend.Append([CreateEvent(new TestEventA("a"))]);
        await backend.Append([CreateEvent(new TestEventA("b"))]);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new BatchProcessor("batch-proc", new HashSet<string> { "test-event-a" });
        var loop = new ControlLoop(
            processor,
            head,
            backend,
            checkpoints,
            TimeSpan.FromMilliseconds(10),
            100,
            batchMiddlewares: [batchMiddleware.ToMiddleware()],
            executionOptions: new ProcessorExecutionOptions(ProcessorBatchingMode.Required));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        var seenBatches = Assert.Single(batchMiddleware.SeenBatches);
        Assert.Equal([1L, 2L], seenBatches);
        Assert.Empty(processor.ProcessedEvents);
        var processedBatch = Assert.Single(processor.ProcessedBatches);
        Assert.Equal([1L, 2L], processedBatch.Select(e => e.GlobalPosition).ToArray());
    }

    [Fact]
    public async Task FunctionalReactor_ProcessBatchAsync_RunsConcurrently_WhenMaxConcurrencySet()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var syncLock = new Lock();

        var reactor = new FunctionalReactor<TestEventA>(
            "parallel-test",
            async (_, _, ct) =>
            {
                int current;
                lock (syncLock)
                {
                    concurrentCount++;
                    current = concurrentCount;
                    if (current > maxConcurrent) maxConcurrent = current;
                }

                await Task.Delay(50, ct);
                lock (syncLock) { concurrentCount--; }
            },
            maxConcurrency: 5);

        var events = Enumerable.Range(1, 10)
            .Select(i => CreateEnvelope(new TestEventA($"e-{i}"), i))
            .ToList();

        await reactor.ProcessBatchAsync(events, TestContext.Current.CancellationToken);

        Assert.True(maxConcurrent > 1, $"Expected concurrent execution but max concurrent was {maxConcurrent}");
    }

    [Fact]
    public async Task FunctionalReactor_ProcessBatchAsync_IsSequential_WhenMaxConcurrencyIsOne()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var syncLock = new Lock();

        var reactor = new FunctionalReactor<TestEventA>(
            "sequential-test",
            async (_, _, ct) =>
            {
                lock (syncLock)
                {
                    concurrentCount++;
                    if (concurrentCount > maxConcurrent) maxConcurrent = concurrentCount;
                }

                await Task.Delay(10, ct);
                lock (syncLock) { concurrentCount--; }
            },
            maxConcurrency: 1);

        var events = Enumerable.Range(1, 5)
            .Select(i => CreateEnvelope(new TestEventA($"e-{i}"), i))
            .ToList();

        await reactor.ProcessBatchAsync(events, TestContext.Current.CancellationToken);

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public void WithConcurrency_ThrowsWhenBatchingIsExplicitlyDisabled()
    {
        var configurator = new ProcessorExecutionConfigurator();
        configurator.DisableBatching().WithConcurrency(5);

        var exception = Assert.Throws<InvalidOperationException>(() => configurator.Build());
        Assert.Contains("WithConcurrency requires batching", exception.Message);
    }

    [Fact]
    public void WithConcurrency_WorksWithDefaultBatching()
    {
        var configurator = new ProcessorExecutionConfigurator();
        var options = configurator.WithConcurrency(10).Build();

        Assert.Equal(ProcessorBatchingMode.Required, options.BatchingMode);
        Assert.Equal(10, options.MaxConcurrency);
    }

    [Fact]
    public void DefaultConfigurator_UsesRequired()
    {
        var configurator = new ProcessorExecutionConfigurator();
        var options = configurator.Build();

        Assert.Equal(ProcessorBatchingMode.Required, options.BatchingMode);
    }

    #endregion

    #region Helper Methods

    private static IEventEnvelope CreateEnvelope<TEvent>(TEvent @event, long position) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventEnvelope
        {
            Id = Guid.NewGuid(),
            EventType = new EventType(eventTypeId),
            EventData = JsonSerializer.Serialize(@event),
            Tags = [],
            GlobalPosition = position,
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static EventToPersist CreateEvent<TEvent>(TEvent @event) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event)
        };
    }

    #endregion
}
