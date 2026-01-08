using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Unit tests for PollingConsumer.
/// Uses InMemoryEventStoreBackend for isolation.
/// </summary>
public sealed class PollingConsumerTests
{
    #region Test Events

    [EventType("test-event-a")]
    public record TestEventA(string Value) : IEvent;

    [EventType("test-event-b")]
    public record TestEventB(int Number) : IEvent;

    #endregion

    #region Test Processor

    private class TestProcessor : IEventProcessor
    {
        private readonly ICheckpointStore _checkpointStore;
        public List<IEventEnvelope> ProcessedEvents { get; } = [];

        public TestProcessor(
            string processorId,
            IReadOnlySet<string> handledTypes,
            ICheckpointStore checkpointStore)
        {
            ProcessorId = processorId;
            HandledEventTypes = handledTypes;
            _checkpointStore = checkpointStore;
        }

        public string ProcessorId { get; }
        public bool IsActive { get; set; } = true;
        public IReadOnlySet<string> HandledEventTypes { get; }

        public async Task<ProcessingResult> ProcessBatchAsync(
            IReadOnlyList<IEventEnvelope> events,
            CancellationToken ct = default)
        {
            ProcessedEvents.AddRange(events);

            if (events.Count > 0)
            {
                await _checkpointStore.SaveAsync(ProcessorId, events[^1].GlobalPosition, ct);
            }

            return ProcessingResult.Continue;
        }
    }

    #endregion

    #region Polling Tests

    [Fact]
    public async Task StartAsync_ShouldProcessExistingEvents()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();
        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer",
            pollingInterval: TimeSpan.FromMilliseconds(10));

        var processor = new TestProcessor(
            "test-processor",
            new HashSet<string> { "test-event-a" },
            checkpointStore);
        consumer.RegisterProcessor(processor);

        // Append event before starting consumer
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("hello"))], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await consumer.StartAsync(cts.Token);

        // Wait for processing
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(processor.ProcessedEvents);
    }

    [Fact]
    public async Task Consumer_ShouldOnlyRouteRelevantEventTypes()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();
        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer",
            pollingInterval: TimeSpan.FromMilliseconds(10));

        var processorA = new TestProcessor(
            "processor-a",
            new HashSet<string> { "test-event-a" },
            checkpointStore);

        var processorB = new TestProcessor(
            "processor-b",
            new HashSet<string> { "test-event-b" },
            checkpointStore);

        consumer.RegisterProcessor(processorA);
        consumer.RegisterProcessor(processorB);

        // Append events of both types
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("hello"))], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append("tenant-1", [CreateEvent(new TestEventB(42))], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await consumer.StartAsync(cts.Token);

        // Wait for processing
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(processorA.ProcessedEvents);
        Assert.Equal("test-event-a", processorA.ProcessedEvents[0].EventType.Id);

        Assert.Single(processorB.ProcessedEvents);
        Assert.Equal("test-event-b", processorB.ProcessedEvents[0].EventType.Id);
    }

    [Fact]
    public async Task Consumer_ShouldProcessEventsAppendedWhileRunning()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();
        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer",
            pollingInterval: TimeSpan.FromMilliseconds(10));

        var processor = new TestProcessor(
            "test-processor",
            new HashSet<string> { "test-event-a" },
            checkpointStore);
        consumer.RegisterProcessor(processor);

        using var cts = new CancellationTokenSource();
        await consumer.StartAsync(cts.Token);

        // Append event while consumer is running
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("hello"))], cancellationToken: TestContext.Current.CancellationToken);

        // Wait for processing
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(processor.ProcessedEvents);
    }

    [Fact]
    public async Task Consumer_ShouldResumeFromCheckpoint()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();

        // Append two events
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("first"))], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("second"))], cancellationToken: TestContext.Current.CancellationToken);

        // Save checkpoint after first event
        await checkpointStore.SaveAsync("test-processor", 1, TestContext.Current.CancellationToken);

        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer",
            pollingInterval: TimeSpan.FromMilliseconds(10));

        var processor = new TestProcessor(
            "test-processor",
            new HashSet<string> { "test-event-a" },
            checkpointStore);
        consumer.RegisterProcessor(processor);

        using var cts = new CancellationTokenSource();
        await consumer.StartAsync(cts.Token);

        // Wait for processing
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        // Should only process the second event (position 2)
        Assert.Single(processor.ProcessedEvents);
        Assert.Equal(2, processor.ProcessedEvents[0].GlobalPosition);
    }

    [Fact]
    public async Task Consumer_ShouldNotProcessEventsForInactiveProcessors()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();
        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer",
            pollingInterval: TimeSpan.FromMilliseconds(10));

        var processor = new TestProcessor(
            "test-processor",
            new HashSet<string> { "test-event-a" },
            checkpointStore)
        {
            IsActive = false
        };
        consumer.RegisterProcessor(processor);

        await backend.Append("tenant-1", [CreateEvent(new TestEventA("hello"))], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await consumer.StartAsync(cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(processor.ProcessedEvents);
    }

    [Fact]
    public async Task StopAsync_ShouldBeIdempotent()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();
        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer");

        await consumer.StopAsync(TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        // No exception means success
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ShouldThrow()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();
        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer");

        using var cts = new CancellationTokenSource();
        await consumer.StartAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => consumer.StartAsync(cts.Token));

        await cts.CancelAsync();
        await consumer.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Consumer_ShouldHandleMultipleProcessorsWithDifferentCheckpoints()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpointStore = new InMemoryCheckpointStore();

        // Append 3 events
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("first"))], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("second"))], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append("tenant-1", [CreateEvent(new TestEventA("third"))], cancellationToken: TestContext.Current.CancellationToken);

        // Processor A is at position 1, B is at position 2
        await checkpointStore.SaveAsync("processor-a", 1, TestContext.Current.CancellationToken);
        await checkpointStore.SaveAsync("processor-b", 2, TestContext.Current.CancellationToken);

        var consumer = new PollingConsumer(
            backend,
            checkpointStore,
            "test-consumer",
            pollingInterval: TimeSpan.FromMilliseconds(10));

        var processorA = new TestProcessor(
            "processor-a",
            new HashSet<string> { "test-event-a" },
            checkpointStore);

        var processorB = new TestProcessor(
            "processor-b",
            new HashSet<string> { "test-event-a" },
            checkpointStore);

        consumer.RegisterProcessor(processorA);
        consumer.RegisterProcessor(processorB);

        using var cts = new CancellationTokenSource();
        await consumer.StartAsync(cts.Token);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        // Processor A should get events 2 and 3
        Assert.Equal(2, processorA.ProcessedEvents.Count);

        // Processor B should get event 3 only
        Assert.Single(processorB.ProcessedEvents);
    }

    #endregion

    #region Helper Methods

    private static EventToPersist CreateEvent<TEvent>(TEvent @event, string tenantId = "tenant-1") where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            TenantId = tenantId,
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event)
        };
    }

    #endregion
}
