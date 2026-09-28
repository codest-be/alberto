using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests for <see cref="ProcessorHealthState"/>, <see cref="ProcessorHealthCheck"/>, the
/// <see cref="ControlLoop"/> reporting that feeds them, and the module registration that
/// wires it all into the host's health-check pipeline.
/// </summary>
public class ProcessorHealthTests
{
    private static readonly TimeSpan Staleness = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_processor_that_never_reported_is_no_news()
    {
        var state = new ProcessorHealthState();

        state.Get("orders-proj").HasReported.Should().BeFalse();
        state.ProcessorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Silence_is_healthy()
    {
        // A standby replica that did not win the lease never starts its loop; that is
        // normal, not an outage.
        var result = await Check(new ProcessorHealthState(), new FakeTimeProvider());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_faulted_processor_is_unhealthy()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: true, lag: 0, time.GetUtcNow());

        var result = await Check(state, time);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("orders-proj").And.Contain("faulted");
        result.Data["orders-proj"].Should().Be("faulted");
    }

    [Fact]
    public async Task A_stale_heartbeat_is_unhealthy()
    {
        // A handler wedged on a call that never returns produces no fault and no
        // heartbeat — staleness is the only signal.
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: false, lag: 0, time.GetUtcNow());

        time.Advance(Staleness + TimeSpan.FromSeconds(1));
        var result = await Check(state, time);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("wedge");
    }

    [Fact]
    public async Task A_fresh_heartbeat_is_healthy()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: false, lag: 3, time.GetUtcNow());

        time.Advance(Staleness); // exactly at the threshold, not past it
        var result = await Check(state, time);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["orders-proj"].Should().Be("healthy");
    }

    [Fact]
    public async Task Lag_beyond_the_threshold_degrades()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: false, lag: 101, time.GetUtcNow());

        var result = await Check(state, time, degradedLagThreshold: 100);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("101");
    }

    [Fact]
    public async Task Unhealthy_wins_over_degraded()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("faulted-one", isFaulted: true, lag: 0, time.GetUtcNow());
        state.Report("lagging-one", isFaulted: false, lag: 500, time.GetUtcNow());

        var result = await Check(state, time, degradedLagThreshold: 100);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["faulted-one"].Should().Be("faulted");
        result.Data["lagging-one"].Should().Be("lagging (500)");
    }

    [Fact]
    public async Task A_running_loop_heartbeats_into_the_state()
    {
        var backend = new InMemoryEventStoreBackend();
        var state = new ProcessorHealthState();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new CountingProcessor("counting");
        var loop = new ControlLoop(processor, head, backend, new InMemoryCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100, healthState: state);

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => state.Get("counting").HasReported, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        var snapshot = state.Get("counting");
        snapshot.IsFaulted.Should().BeFalse();
        snapshot.LastHeartbeatAt.Should().BeAfter(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task A_faulting_loop_reports_the_fault()
    {
        var backend = new InMemoryEventStoreBackend();
        var state = new ProcessorHealthState();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new ThrowingProcessor("throwing");
        var loop = new ControlLoop(processor, head, backend, new InMemoryCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100, healthState: state);

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => state.Get("throwing").IsFaulted, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        state.Get("throwing").IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task The_module_registration_wires_state_and_check()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());
        await using var sp = services.BuildServiceProvider();

        sp.GetKeyedService<ProcessorHealthState>("orders").Should().NotBeNull();

        var registration = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Should()
            .ContainSingle(r => r.Name == "alberto-processors-orders").Subject;

        registration.Tags.Should().Contain("alberto").And.Contain("processors");

        // The factory must resolve, and an idle module with no reporting loops is healthy.
        var check = registration.Factory(sp);
        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration },
            TestContext.Current.CancellationToken);
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    private static Task<HealthCheckResult> Check(
        ProcessorHealthState state, TimeProvider time, long degradedLagThreshold = long.MaxValue) =>
        new ProcessorHealthCheck("orders", state, Staleness, degradedLagThreshold, time)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

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

    private static EventToPersist Event() => new()
    {
        EventType = new EventType("processor-health-test-event"),
        Tags = [],
        EventData = System.Text.Json.JsonSerializer.Serialize(new HealthTestEvent("x")),
    };

    [EventType("processor-health-test-event")]
    public record HealthTestEvent(string Value) : IEvent;

    private sealed class CountingProcessor(string processorId) : IBatchableProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "processor-health-test-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingProcessor(string processorId) : IBatchableProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "processor-health-test-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated fault");

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated fault");
    }
}
