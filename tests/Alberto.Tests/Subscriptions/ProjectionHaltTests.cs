#pragma warning disable ALB9002 // Health state is experimental.
using System.Text.Json;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// A projection never dead-letters and never moves past an event it could not apply: it stays on
/// that event, retries it in place on the halt backoff, reports itself halted, and carries on in
/// order once the failure clears. Backoff waits run on a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class ProjectionHaltTests
{
    private const string ModuleKey = "halt-test";

    [EventType("halt-test-event")]
    private sealed record Item(string Value) : IEvent;

    private sealed class FlakyProcessor(string processorId) : IBatchableProcessor
    {
        private readonly object _gate = new();

        public string ProcessorId { get; } = processorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { "halt-test-event" };

        /// <summary>The value that throws while <see cref="Broken"/> is set.</summary>
        public string PoisonValue { get; init; } = "poison";

        public volatile bool Broken = true;

        public List<string> Applied { get; } = [];
        public int PoisonAttempts;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default)
        {
            Apply([Value(@event)]);
            return Task.CompletedTask;
        }

        // All-or-nothing, so a bisected batch never applies an event twice.
        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default)
        {
            Apply(events.Select(Value).ToList());
            return Task.CompletedTask;
        }

        private void Apply(List<string> values)
        {
            lock (_gate)
            {
                if (values.Contains(PoisonValue))
                {
                    Interlocked.Increment(ref PoisonAttempts);
                    if (Broken)
                        throw new InvalidOperationException("read model unavailable");
                }

                Applied.AddRange(values);
            }
        }

        private static string Value(IEventEnvelope e) => JsonSerializer.Deserialize<Item>(e.EventData)!.Value;

        public string[] Snapshot()
        {
            lock (_gate) return [.. Applied];
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required InMemoryEventStoreBackend Backend { get; init; }
        public required InMemoryCheckpointStore Checkpoints { get; init; }
        public required InMemoryDeadLetterStore DeadLetters { get; init; }
        public required EventStoreHead Head { get; init; }
        public required ProcessorHealthState Health { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public required FlakyProcessor Processor { get; init; }
        public required ControlLoop Loop { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Loop.StopAsync(CancellationToken.None);
            await Head.StopAsync(CancellationToken.None);
        }

        public Task AppendAsync(params string[] values) =>
            Backend.AppendAsync(
                [.. values.Select(v => new EventToPersist
                {
                    EventType = new EventType("halt-test-event"),
                    Tags = [],
                    EventData = JsonSerializer.Serialize(new Item(v)),
                })],
                cancellationToken: TestContext.Current.CancellationToken);

        /// <summary>Advances the fake clock in small steps until <paramref name="condition"/> holds.</summary>
        public async Task PumpUntilAsync(Func<bool> condition, string because)
        {
            for (var i = 0; i < 4000; i++)
            {
                if (condition())
                    return;

                Clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"Condition never held: {because}");
        }
    }

    private static async Task<Rig> StartAsync(
        ProcessorKind kind,
        bool batching,
        ProcessorExecutionOptions? execution = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var deadLetters = new InMemoryDeadLetterStore();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var health = new ProcessorHealthState();
        var processor = new FlakyProcessor("flaky");
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));

        execution ??= new ProcessorExecutionOptions
        {
            BatchingMode = batching ? ProcessorBatchingMode.Required : ProcessorBatchingMode.Disabled,
        };

        var module = new ControlLoopOptions { Retry = new RetryOptions { MaxRetries = 1, RetryDelay = TimeSpan.FromSeconds(1) } };
        var policies = new ProcessorPolicies(module,
        [
            new ProcessorDeclaration
            {
                ProcessorId = processor.ProcessorId,
                Kind = kind,
                Execution = execution,
            },
        ]);

        var assembler = new ControlLoopAssembler(
            [], [], module.Retry, DefaultErrorClassifier.Instance, deadLetters, clock,
            policies: policies);

        var loop = assembler.Create(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 100, moduleKey: ModuleKey,
            executionOptions: execution, healthState: health);

        await head.StartAsync(cancellationToken);
        await loop.StartAsync(cancellationToken);

        return new Rig
        {
            Backend = backend,
            Checkpoints = checkpoints,
            DeadLetters = deadLetters,
            Head = head,
            Health = health,
            Clock = clock,
            Processor = processor,
            Loop = loop,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projection_halts_on_failing_event_and_resumes_in_order(bool batching)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ProcessorKind.Projection, batching);

        // Appended together so the batch path has to isolate the poison event by bisecting.
        await rig.AppendAsync("a", "b", "poison", "c", "d");

        await rig.PumpUntilAsync(() => rig.Loop.IsFaulted, "the processor halts on the failing event");
        var attemptsWhenHalted = rig.Processor.PoisonAttempts;
        await rig.PumpUntilAsync(() => rig.Processor.PoisonAttempts >= attemptsWhenHalted + 3, "in-place retries of the failing event");

        // Halted: stuck on the event, nothing later applied, nothing dead-lettered, reported.
        rig.Processor.Snapshot().Should().NotContain(["c", "d"]);
        (await rig.DeadLetters.CountAsync("flaky", cancellationToken)).Should().Be(0);
        (await rig.Checkpoints.GetAsync("flaky", cancellationToken) ?? 0).Should().BeLessThan(3, "the checkpoint must not pass the failing event");
        rig.Loop.IsFaulted.Should().BeTrue();
        rig.Health.Get("flaky").IsFaulted.Should().BeTrue();

        // The failure clears: the same event is applied, then the rest, in order.
        rig.Processor.Broken = false;
        await rig.PumpUntilAsync(() => rig.Processor.Snapshot().Length == 5, "resume after the failure clears");

        rig.Processor.Snapshot().Should().Equal("a", "b", "poison", "c", "d");
        (await rig.DeadLetters.CountAsync("flaky", cancellationToken)).Should().Be(0);
        await rig.PumpUntilAsync(() => !rig.Loop.IsFaulted && !rig.Health.Get("flaky").IsFaulted, "halt flag clears");
    }

    [Fact]
    public async Task Reactor_still_dead_letters_and_moves_on()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ProcessorKind.Reactor, batching: false);

        await rig.AppendAsync("a", "poison", "c");

        await rig.PumpUntilAsync(() => rig.Processor.Snapshot().Length == 2, "events after the poison one are applied");

        rig.Processor.Snapshot().Should().Equal("a", "c");
        (await rig.DeadLetters.CountAsync("flaky", cancellationToken)).Should().Be(1);
        rig.Loop.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task Halt_backoff_follows_per_processor_override_on_the_fake_clock()
    {
        await using var rig = await StartAsync(
            ProcessorKind.Projection, batching: false,
            execution: new ProcessorExecutionOptions
            {
                BatchingMode = ProcessorBatchingMode.Disabled,
                Halt = new HaltOverrides { InitialDelay = TimeSpan.FromMinutes(10), MaxDelay = TimeSpan.FromMinutes(10) },
            });

        await rig.AppendAsync("poison");
        await rig.PumpUntilAsync(() => rig.Processor.PoisonAttempts >= 2, "first fast retry");
        var before = rig.Processor.PoisonAttempts;

        // Nothing happens before the configured wait elapses.
        rig.Clock.Advance(TimeSpan.FromMinutes(9));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        rig.Processor.PoisonAttempts.Should().Be(before);

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.PumpUntilAsync(() => rig.Processor.PoisonAttempts > before, "retry after the override delay");
    }

    [Fact]
    public void Policies_resolve_kind_overrides_and_shadow_rebuild_ids()
    {
        var module = new ControlLoopOptions();
        var policies = new ProcessorPolicies(module,
        [
            new ProcessorDeclaration { ProcessorId = "p", Kind = ProcessorKind.Projection },
            new ProcessorDeclaration { ProcessorId = "r", Kind = ProcessorKind.Reactor },
            new ProcessorDeclaration { ProcessorId = "o", Kind = ProcessorKind.Reactor, HaltsOnFailure = true },
            new ProcessorDeclaration
            {
                ProcessorId = "tuned",
                Kind = ProcessorKind.Reactor,
                Execution = ProcessorExecutionOptions.Default with { Retry = new RetryOverrides { MaxRetries = 9 } },
            },
        ]);

        policies.For("p").HaltsOnFailure.Should().BeTrue();
        policies.For("p::rebuild::2").HaltsOnFailure.Should().BeTrue();
        policies.For("o").HaltsOnFailure.Should().BeTrue();
        policies.For("r").HaltsOnFailure.Should().BeFalse();
        policies.For("unknown").HaltsOnFailure.Should().BeFalse();
        policies.For("tuned").Retry.MaxRetries.Should().Be(9);
        policies.For("tuned").Retry.RetryDelay.Should().Be(module.Retry.RetryDelay);
    }
}
