using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for the consume middleware pipeline integrated into <see cref="ControlLoop"/>.
/// Validates that the default retry-and-dead-letter middleware keeps the loop running
/// when a processor faults on a single event, instead of halting at that checkpoint.
/// </summary>
public sealed class ControlLoopMiddlewareTests
{
    [EventType("middleware-test-event")]
    public record TestEvent(string Value) : IEvent;

    private static EventToPersist CreateEvent(TestEvent @event)
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TestEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event),
        };
    }

    /// <summary>
    /// Processor that throws on the first N events whose payload matches a predicate,
    /// then succeeds on everything else. Tracks invocation count per envelope.
    /// </summary>
    private sealed class SelectivelyFaultingProcessor(
        string processorId,
        Func<IEventEnvelope, bool> shouldFault)
        : IEventProcessor
    {
        public List<IEventEnvelope> ProcessedEvents { get; } = [];
        public Dictionary<long, int> AttemptsByPosition { get; } = [];

        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "middleware-test-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            AttemptsByPosition[@event.GlobalPosition] =
                AttemptsByPosition.GetValueOrDefault(@event.GlobalPosition) + 1;

            if (shouldFault(@event))
                throw new InvalidOperationException("Simulated permanent fault");

            ProcessedEvents.Add(@event);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task BadEventIsDeadLettered_LoopContinues_CheckpointAdvances()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var deadLetters = new InMemoryDeadLetterStore();

        // Position 1 = poison, positions 2..3 should still be processed
        await backend.Append([CreateEvent(new TestEvent("poison"))], cancellationToken: ct);
        await backend.Append([CreateEvent(new TestEvent("ok-1"))], cancellationToken: ct);
        await backend.Append([CreateEvent(new TestEvent("ok-2"))], cancellationToken: ct);

        var processor = new SelectivelyFaultingProcessor(
            "selective",
            e => e.GlobalPosition == 1);

        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            new ErrorPolicy { MaxRetries = 0 }, // InvalidOperationException is Permanent → 1 attempt
            deadLetters);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100,
            moduleKey: "test",
            middlewares: [middleware]);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(250, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        // Loop should NOT be faulted — DLQ swallowed the poison event
        Assert.False(loop.IsFaulted);

        // Both healthy events were processed
        Assert.Equal(2, processor.ProcessedEvents.Count);
        Assert.All(processor.ProcessedEvents, e => Assert.NotEqual(1, e.GlobalPosition));

        // Checkpoint advanced past the poison event
        var checkpoint = await checkpoints.GetAsync("selective", ct);
        Assert.Equal(3, checkpoint);

        // Poison event landed in dead-letter storage
        var entries = await deadLetters.GetAsync("selective", ct: ct);
        var entry = Assert.Single(entries);
        Assert.Equal("selective", entry.ProcessorId);
        Assert.Equal(1, entry.GlobalPosition);
        Assert.Contains("Simulated permanent fault", entry.ErrorMessage);
    }

    [Fact]
    public async Task PermanentError_DoesNotRetry()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var deadLetters = new InMemoryDeadLetterStore();
        var ct = TestContext.Current.CancellationToken;

        await backend.Append([CreateEvent(new TestEvent("poison"))], cancellationToken: ct);

        var processor = new SelectivelyFaultingProcessor("perm", _ => true);

        // Even though MaxRetries=5, InvalidOperationException is classified Permanent,
        // so the middleware skips retries and dead-letters after a single attempt.
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            new ErrorPolicy { MaxRetries = 5, RetryDelay = TimeSpan.FromMilliseconds(1) },
            deadLetters);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100,
            moduleKey: "test",
            middlewares: [middleware]);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.False(loop.IsFaulted);
        Assert.Equal(1, processor.AttemptsByPosition[1]); // exactly one attempt — no retries
        Assert.Equal(1, await deadLetters.CountAsync("perm", ct));
    }

    [Fact]
    public async Task TransientError_RetriesUpToMaxThenDeadLetters()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var deadLetters = new InMemoryDeadLetterStore();
        var ct = TestContext.Current.CancellationToken;

        await backend.Append([CreateEvent(new TestEvent("transient"))], cancellationToken: ct);

        var attempts = 0;
        var processor = new ThrowingProcessor("trans", () =>
        {
            attempts++;
            // TimeoutException → classified as Transient → retried
            throw new TimeoutException("simulated timeout");
        });

        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            new ErrorPolicy
            {
                MaxRetries = 2,
                RetryDelay = TimeSpan.FromMilliseconds(1),
                BackoffMultiplier = 1.0,
            },
            deadLetters);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var loop = new ControlLoop(processor, head, backend, checkpoints,
            TimeSpan.FromMilliseconds(10), 100,
            moduleKey: "test",
            middlewares: [middleware]);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.False(loop.IsFaulted);
        Assert.Equal(3, attempts); // 1 initial + 2 retries
        Assert.Equal(1, await deadLetters.CountAsync("trans", ct));

        var checkpoint = await checkpoints.GetAsync("trans", ct);
        Assert.Equal(1, checkpoint);
    }

    private sealed class ThrowingProcessor(string processorId, Action onProcess) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "middleware-test-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            onProcess();
            return Task.CompletedTask;
        }
    }

    private sealed class BatchSelectiveProcessor(
        string processorId,
        Func<IEventEnvelope, bool> shouldFault) : IBatchableProcessor
    {
        public List<IEventEnvelope> ProcessedEvents { get; } = [];
        public List<IReadOnlyList<IEventEnvelope>> ProcessedBatches { get; } = [];
        public Dictionary<string, int> AttemptsByBatch { get; } = [];

        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "middleware-test-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            ProcessedEvents.Add(@event);
            return Task.CompletedTask;
        }

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
        {
            var key = string.Join(",", events.Select(e => e.GlobalPosition));
            AttemptsByBatch[key] = AttemptsByBatch.GetValueOrDefault(key) + 1;

            if (events.Any(shouldFault))
                throw new InvalidOperationException("Simulated permanent fault");

            ProcessedBatches.Add(events.ToList());
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task BatchedPoisonEvent_IsolatedDeadLettered_HealthyEventsStillAdvance()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var deadLetters = new InMemoryDeadLetterStore();

        await backend.Append([CreateEvent(new TestEvent("poison"))], cancellationToken: ct);
        await backend.Append([CreateEvent(new TestEvent("ok-1"))], cancellationToken: ct);
        await backend.Append([CreateEvent(new TestEvent("ok-2"))], cancellationToken: ct);

        var processor = new BatchSelectiveProcessor(
            "batch-selective",
            e => e.GlobalPosition == 1);

        var middleware = BatchConsumeMiddlewares.RetryAndDeadLetter(
            new ErrorPolicy { MaxRetries = 0 },
            deadLetters);

        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var loop = new ControlLoop(
            processor,
            head,
            backend,
            checkpoints,
            TimeSpan.FromMilliseconds(10),
            100,
            moduleKey: "test",
            batchMiddlewares: [middleware],
            executionOptions: new ProcessorExecutionOptions(ProcessorBatchingMode.Required));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await Task.Delay(250, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.False(loop.IsFaulted);
        Assert.Equal([2L, 3L], processor.ProcessedBatches.SelectMany(b => b.Select(e => e.GlobalPosition)).Order().ToArray());
        Assert.Empty(processor.ProcessedEvents);

        var checkpoint = await checkpoints.GetAsync("batch-selective", ct);
        Assert.Equal(3, checkpoint);

        var entries = await deadLetters.GetAsync("batch-selective", ct: ct);
        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.GlobalPosition);
        Assert.Contains("Simulated permanent fault", entry.ErrorMessage);
    }
}
