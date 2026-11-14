using System.Text.Json;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.DeadLetters;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// A reactor's dead letter with a redeliverable error comes back by itself on a growing
/// schedule, then stays parked for manual retry. Everything runs on a
/// <see cref="FakeTimeProvider"/>: the tests move the clock, they never sleep for a schedule.
/// </summary>
public sealed class ReactorRedeliveryTests
{
    private const string ModuleKey = "redelivery-test";
    private const string Id = "reactor";

    [EventType("redelivery-test-event")]
    private sealed record Item(string Value) : IEvent;

    private sealed class FlakyReactor : IBatchableProcessor
    {
        public string ProcessorId => Id;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { "redelivery-test-event" };

        /// <summary>What every delivery throws while set. Null means the handler works.</summary>
        public volatile Exception? Failure = new TimeoutException("downstream timed out");

        public int Deliveries;
        public List<string> Handled { get; } = [];

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default) => Handle([@event]);

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default) => Handle(events);

        private Task Handle(IReadOnlyList<IEventEnvelope> events)
        {
            Interlocked.Increment(ref Deliveries);
            if (Failure is { } failure)
                throw failure;

            lock (Handled)
                Handled.AddRange(events.Select(e => JsonSerializer.Deserialize<Item>(e.EventData)!.Value));
            return Task.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required InMemoryEventStoreBackend Backend { get; init; }
        public required InMemoryDeadLetterStore DeadLetters { get; init; }
        public required EventStoreHead Head { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public required FlakyReactor Reactor { get; init; }
        public required ControlLoop Loop { get; init; }
        public required DeadLetterRetryLoop RetryLoop { get; init; }

        public async ValueTask DisposeAsync()
        {
            await RetryLoop.StopAsync(CancellationToken.None);
            await Loop.StopAsync(CancellationToken.None);
            await Head.StopAsync(CancellationToken.None);
        }

        public Task AppendAsync(params string[] values) =>
            Backend.AppendAsync(
                [.. values.Select(v => new EventToPersist
                {
                    EventType = new EventType("redelivery-test-event"),
                    Tags = [],
                    EventData = JsonSerializer.Serialize(new Item(v)),
                })],
                cancellationToken: TestContext.Current.CancellationToken);

        public async Task<IReadOnlyList<DeadLetterEntry>> RowsAsync() =>
            await DeadLetters.GetAsync(Id, null, 100, TestContext.Current.CancellationToken);

        /// <summary>Advances the fake clock in steps until <paramref name="condition"/> holds.</summary>
        public async Task PumpUntilAsync(Func<Task<bool>> condition, string because, TimeSpan? step = null)
        {
            for (var i = 0; i < 6000; i++)
            {
                if (await condition())
                    return;

                Clock.Advance(step ?? TimeSpan.FromSeconds(5));
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"Condition never held: {because}");
        }
    }

    private static async Task<Rig> StartAsync(
        bool batching = false,
        RedeliveryOptions? moduleRedelivery = null,
        ProcessorExecutionOptions? execution = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var backend = new InMemoryEventStoreBackend();
        var deadLetters = new InMemoryDeadLetterStore();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var reactor = new FlakyReactor();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));

        execution ??= new ProcessorExecutionOptions
        {
            BatchingMode = batching ? ProcessorBatchingMode.Required : ProcessorBatchingMode.Disabled,
        };

        var module = new ControlLoopOptions
        {
            Retry = new RetryOptions { MaxRetries = 1, RetryDelay = TimeSpan.FromSeconds(1) },
            Redelivery = moduleRedelivery ?? new RedeliveryOptions(),
        };
        var declarations = new[]
        {
            new ProcessorDeclaration { ProcessorId = Id, Kind = ProcessorKind.Reactor, Execution = execution },
        };

        var assembler = new ControlLoopAssembler(
            [], [], module.Retry, DefaultErrorClassifier.Instance, deadLetters, clock,
            policies: new ProcessorPolicies(module, declarations));

        var loop = assembler.Create(
            reactor, head, backend, new InMemoryCheckpointStore(),
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 100, moduleKey: ModuleKey,
            executionOptions: execution);

        var retryLoop = new DeadLetterRetryLoop(
            reactor,
            deadLetters,
            pollingInterval: TimeSpan.FromSeconds(10),
            middlewares:
            [
                ConsumeMiddlewares.RetryAndDeadLetterOn(
                    AlbertoMetrics.Shared, module.Retry, DefaultErrorClassifier.Instance, deadLetters, clock, null,
                    new ProcessorPolicies(module, declarations, retryLoop: true)),
            ],
            timeProvider: clock,
            drainTimeout: TimeSpan.FromSeconds(1));

        await head.StartAsync(cancellationToken);
        await loop.StartAsync(cancellationToken);
        await retryLoop.StartAsync(cancellationToken);

        return new Rig
        {
            Backend = backend,
            DeadLetters = deadLetters,
            Head = head,
            Clock = clock,
            Reactor = reactor,
            Loop = loop,
            RetryLoop = retryLoop,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transient_failure_is_dead_lettered_with_a_next_retry_and_redelivered_when_due(bool batching)
    {
        await using var rig = await StartAsync(batching);

        await rig.AppendAsync("poison");
        await rig.PumpUntilAsync(async () => (await rig.RowsAsync()).Count == 1, "the event is dead-lettered", TimeSpan.FromSeconds(1));

        var row = (await rig.RowsAsync()).Single();
        row.NextRetryAt.Should().Be(row.FailedAt + TimeSpan.FromMinutes(1), "the first redelivery waits the first delay");
        row.RedeliveryCount.Should().Be(0);

        // Not due yet: the clock is still short of next_retry_at, so nothing is dispatched again.
        var deliveries = rig.Reactor.Deliveries;
        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        rig.Reactor.Deliveries.Should().Be(deliveries);

        // The failure clears, the clock reaches next_retry_at, the event is handled and the row is gone.
        rig.Reactor.Failure = null;
        await rig.PumpUntilAsync(async () => (await rig.RowsAsync()).Count == 0, "the redelivery succeeds");

        rig.Reactor.Handled.Should().Equal("poison");
    }

    [Fact]
    public async Task Repeat_failure_keeps_the_row_grows_the_wait_and_parks_after_the_last_redelivery()
    {
        await using var rig = await StartAsync(
            moduleRedelivery: new RedeliveryOptions { MaxRedeliveries = 2 });

        await rig.AppendAsync("poison");

        var seen = new Dictionary<int, (Guid Id, TimeSpan? Wait)>();
        await rig.PumpUntilAsync(async () =>
        {
            foreach (var r in await rig.RowsAsync())
                seen[r.RedeliveryCount] = (r.Id, r.NextRetryAt - r.FailedAt);
            return seen.ContainsKey(2);
        }, "both redeliveries fail", TimeSpan.FromSeconds(5));

        // The same row all the way, waiting 1 min, then 4 min, then parked.
        seen.Values.Select(v => v.Id).Distinct().Should().HaveCount(1);
        seen[0].Wait.Should().Be(TimeSpan.FromMinutes(1));
        seen[1].Wait.Should().Be(TimeSpan.FromMinutes(4));
        seen[2].Wait.Should().BeNull("the last redelivery failed, so the row stays for manual retry");

        // Parked: days pass and nothing is dispatched.
        await rig.PumpUntilAsync(async () => await Task.FromResult(true), "settle");
        var deliveries = rig.Reactor.Deliveries;
        rig.Clock.Advance(TimeSpan.FromDays(5));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        rig.Reactor.Deliveries.Should().Be(deliveries);
        (await rig.RowsAsync()).Should().ContainSingle().Which.NextRetryAt.Should().BeNull();

        // A manual retry still redelivers the exhausted row.
        rig.Reactor.Failure = null;
        await rig.DeadLetters.MarkForRetryAsync(Id, TestContext.Current.CancellationToken);
        await rig.PumpUntilAsync(async () => (await rig.RowsAsync()).Count == 0, "manual retry of the exhausted row");
        rig.Reactor.Handled.Should().Equal("poison");
    }

    [Fact]
    public async Task Permanent_error_is_parked_at_once()
    {
        await using var rig = await StartAsync();
        rig.Reactor.Failure = new ArgumentException("bad payload");

        await rig.AppendAsync("poison");
        await rig.PumpUntilAsync(async () => (await rig.RowsAsync()).Count == 1, "the event is dead-lettered", TimeSpan.FromSeconds(1));

        (await rig.RowsAsync()).Single().NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_error_is_not_redelivered_when_only_transient_ones_are()
    {
        await using var rig = await StartAsync(moduleRedelivery: new RedeliveryOptions { RedeliverWhen = RedeliverWhen.Transient });
        rig.Reactor.Failure = new InvalidOperationException("who knows");

        await rig.AppendAsync("poison");
        await rig.PumpUntilAsync(async () => (await rig.RowsAsync()).Count == 1, "the event is dead-lettered", TimeSpan.FromSeconds(1));

        (await rig.RowsAsync()).Single().NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task Per_processor_override_beats_the_module_default()
    {
        await using var rig = await StartAsync(
            moduleRedelivery: new RedeliveryOptions { FirstDelay = TimeSpan.FromMinutes(1) },
            execution: new ProcessorExecutionOptions
            {
                BatchingMode = ProcessorBatchingMode.Disabled,
                Redelivery = new RedeliveryOverrides { FirstDelay = TimeSpan.FromHours(2) },
            });

        await rig.AppendAsync("poison");
        await rig.PumpUntilAsync(async () => (await rig.RowsAsync()).Count == 1, "the event is dead-lettered", TimeSpan.FromSeconds(1));

        var row = (await rig.RowsAsync()).Single();
        (row.NextRetryAt - row.FailedAt).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task Configuration_overrides_apply_per_processor_and_disabled_parks_at_once()
    {
        // The shape Processors:<id>:Redelivery binds to.
        var execution = new ProcessorExecutionOverrides { Redelivery = new RedeliveryOverrides { Enabled = false } }
            .ApplyTo(new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled });

        await using var rig = await StartAsync(execution: execution);

        await rig.AppendAsync("poison");
        await rig.PumpUntilAsync(async () => (await rig.RowsAsync()).Count == 1, "the event is dead-lettered", TimeSpan.FromSeconds(1));

        (await rig.RowsAsync()).Single().NextRetryAt.Should().BeNull();
    }

    [Fact]
    public void Default_schedule_is_ten_redeliveries_over_about_two_and_a_half_days()
    {
        var options = new RedeliveryOptions();
        var waits = Enumerable.Range(1, options.MaxRedeliveries).Select(options.CalculateDelay).ToArray();

        waits[0].Should().Be(TimeSpan.FromMinutes(1));
        waits[1].Should().Be(TimeSpan.FromMinutes(4));
        waits[2].Should().Be(TimeSpan.FromMinutes(16));
        waits[5].Should().Be(TimeSpan.FromHours(12));
        waits[9].Should().Be(TimeSpan.FromHours(12));
        TimeSpan.FromTicks(waits.Sum(w => w.Ticks)).TotalHours.Should().BeApproximately(65.7, 0.1);
    }
}
