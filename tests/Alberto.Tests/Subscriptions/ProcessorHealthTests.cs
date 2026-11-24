using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
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

        Assert.False(state.Get("orders-proj").HasReported);
        Assert.Empty(state.ProcessorIds);
    }

    [Fact]
    public async Task Silence_is_healthy()
    {
        // A standby replica that did not win the lease never starts its loop; that is
        // normal, not an outage.
        var result = await Check(new ProcessorHealthState(), new FakeTimeProvider());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task A_faulted_processor_is_unhealthy()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: true, lag: 0, time.GetUtcNow());

        var result = await Check(state, time);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("orders-proj", result.Description);
        Assert.Contains("faulted", result.Description);
        Assert.Equal("faulted", result.Data["orders-proj"]);
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

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("wedge", result.Description);
    }

    [Fact]
    public async Task A_fresh_heartbeat_is_healthy()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: false, lag: 3, time.GetUtcNow());

        time.Advance(Staleness); // exactly at the threshold, not past it
        var result = await Check(state, time);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("healthy", result.Data["orders-proj"]);
    }

    [Fact]
    public async Task Lag_beyond_the_threshold_degrades()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: false, lag: 101, time.GetUtcNow());

        var result = await Check(state, time, degradedLagThreshold: 100);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("101", result.Description);
    }

    [Fact]
    public async Task Unhealthy_wins_over_degraded()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("faulted-one", isFaulted: true, lag: 0, time.GetUtcNow());
        state.Report("lagging-one", isFaulted: false, lag: 500, time.GetUtcNow());

        var result = await Check(state, time, degradedLagThreshold: 100);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("faulted", result.Data["faulted-one"]);
        Assert.Equal("lagging (500)", result.Data["lagging-one"]);
    }

    [Fact]
    public async Task A_running_loop_heartbeats_into_the_state()
    {
        var backend = new InMemoryEventStoreBackend();
        var state = new ProcessorHealthState();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new CountingProcessor("counting");
        var loop = new ControlLoop(processor, head, backend, new InMemoryFencedCheckpointStore(),
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
        Assert.False(snapshot.IsFaulted);
        Assert.True(snapshot.LastHeartbeatAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task A_faulting_loop_reports_the_fault()
    {
        var backend = new InMemoryEventStoreBackend();
        var state = new ProcessorHealthState();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var processor = new ThrowingProcessor("throwing");
        var loop = new ControlLoop(processor, head, backend, new InMemoryFencedCheckpointStore(),
            TimeSpan.FromMilliseconds(10), 100, healthState: state);

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await WaitForAsync(() => state.Get("throwing").IsFaulted, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.True(state.Get("throwing").IsFaulted);
    }

    [Fact]
    public async Task The_module_registration_wires_state_and_check()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());
        await using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetKeyedService<ProcessorHealthState>("orders"));

        var registration = Assert.Single(sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations, r => r.Name == "alberto-processors-orders");

        Assert.Contains("alberto", registration.Tags);
        Assert.Contains("processors", registration.Tags);

        // The factory must resolve, and an idle module with no reporting loops is healthy.
        var check = registration.Factory(sp);
        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration },
            TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private static Task<HealthCheckResult> Check(
        ProcessorHealthState state, TimeProvider time, long degradedLagThreshold = long.MaxValue) =>
        new ProcessorHealthCheck("orders", state, Staleness, degradedLagThreshold, time)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 10s.");
            await Task.Delay(10, cancellationToken);
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

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingProcessor(string processorId) : IBatchableProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "processor-health-test-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated fault");

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated fault");
    }
}
