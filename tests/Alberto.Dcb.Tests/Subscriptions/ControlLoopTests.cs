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

    private class TestProcessor(string processorId, IReadOnlySet<string> handledTypes) : IEventProcessor
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
    }

    private class FaultingProcessor(string processorId, IReadOnlySet<string> handledTypes) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = handledTypes;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated fault");
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

    #endregion

    #region Helper Methods

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
