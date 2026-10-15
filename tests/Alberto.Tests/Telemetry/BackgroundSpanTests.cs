using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Alberto.Append;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// Work Alberto does on its own schedule is traced as root spans that never leak into the caller's
/// trace, and the instruments behind its metrics are scoped to the container.
/// </summary>
[Collection(OtelTracingCollection.Name)]
public sealed class BackgroundSpanTests
{
    private static readonly ActivitySource Outer = new("Alberto.Tests.Outer");

    [EventType("background-span-event")]
    private record BackgroundEvent(string Label) : IEvent;

    private static readonly string EventTypeId = EventTypeAttribute.GetEventTypeId(typeof(BackgroundEvent));

    private sealed class Processor(string processorId) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };
        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ActivityListener Listen(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Alberto" or "Alberto.Tests.Outer",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task<Activity> WaitForSpanAsync(List<Activity> stopped, Func<Activity, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (stopped)
            {
                if (stopped.FirstOrDefault(match) is { } found)
                    return found;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Span not recorded within 10s.");
    }

    [Fact]
    public async Task Control_loop_spans_are_roots_and_do_not_leak_into_the_starting_span()
    {
        const string processorId = "background-root";
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);

        var inner = new InMemoryEventStoreBackend();
        await inner.AppendAsync(
            [new EventToPersist { EventType = new EventType(EventTypeId), Tags = [], EventData = JsonSerializer.Serialize(new BackgroundEvent("a")) }],
            cancellationToken: TestContext.Current.CancellationToken);
        var backend = new InterceptingEventStoreBackend(inner, new AppendInterceptorPipeline([]));
        var head = new EventStoreHead(inner, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            new Processor(processorId), head, backend, new InMemoryCheckpointStore(),
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 100, moduleKey: "orders#eu",
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled });

        using (var outer = Outer.StartActivity("host-start"))
        {
            await head.StartAsync(CancellationToken.None);
            await loop.StartAsync(CancellationToken.None);
            Assert.Same(outer, Activity.Current);
        }

        // Other tests' control loops run in parallel and emit polls of their own: match on ours.
        var poll = await WaitForSpanAsync(stopped, a =>
            a.OperationName == "alberto.poll" && a.GetTagItem("alberto.poll.events") is 1 &&
            a.GetTagItem("alberto.processor.id") as string == processorId);
        var save = await WaitForSpanAsync(stopped, a =>
            a.OperationName == "alberto.checkpoint.save" && a.GetTagItem("alberto.processor.id") as string == processorId);

        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.Null(poll.Parent);
        Assert.Equal(default, poll.ParentSpanId);
        Assert.Equal("orders", poll.GetTagItem("alberto.module"));
        Assert.Equal("eu", poll.GetTagItem("alberto.shard"));
        lock (stopped)
            Assert.Contains(stopped, a => a.OperationName == "alberto.read" && a.ParentSpanId == poll.SpanId);

        Assert.Equal(default, save.ParentSpanId);
        Assert.Equal(1L, save.GetTagItem("alberto.checkpoint.position"));
        Assert.NotEqual(ActivityStatusCode.Ok, save.Status);
    }

    [Fact]
    public async Task Outbox_purge_is_a_root_span()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new OutboxRetentionService(
            new PurgeOnlyStore(), retention: TimeSpan.FromDays(1), sweepInterval: TimeSpan.FromHours(1), timeProvider: time);

        using (Outer.StartActivity("host-start"))
            await service.StartAsync(CancellationToken.None);
        await service.Armed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(1));

        var purge = await WaitForSpanAsync(stopped, a => a.OperationName == "alberto.outbox.purge");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(default, purge.ParentSpanId);
        Assert.NotNull(purge.GetTagItem("alberto.outbox.purge.before"));
    }

    [Fact]
    public void Fail_records_the_type_and_never_the_message()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        const string secret = "password=hunter2";

        using (var span = BackgroundSpans.StartRoot("alberto.test"))
            BackgroundSpans.Fail(span, new TimeoutException(secret));

        var failed = Assert.Single(stopped, a => a.OperationName == "alberto.test");
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Null(failed.StatusDescription);
        Assert.Equal(typeof(TimeoutException).FullName, failed.GetTagItem("error.type"));
        var exception = Assert.Single(failed.Events);
        Assert.Equal([new KeyValuePair<string, object?>("exception.type", typeof(TimeoutException).FullName)], exception.Tags);
    }

    [Fact]
    public void Instruments_come_from_the_meter_factory_when_the_container_has_one()
    {
        var services = new ServiceCollection().AddMetrics();
        AlbertoInstruments.Register(services);
        AlbertoInstruments.Register(services);
        using var provider = services.BuildServiceProvider();

        var instruments = AlbertoInstruments.For(provider);

        Assert.NotSame(AlbertoMetrics.Shared, instruments);
        Assert.NotSame(AlbertoMetrics.Meter, instruments.Meter);
        Assert.Equal(AlbertoMetrics.Name, instruments.Meter.Name);
        Assert.Single(services, d => d.ServiceType == typeof(AlbertoInstruments));
    }

    [Fact]
    public void Instruments_fall_back_to_the_static_meter_without_a_factory()
    {
        var services = new ServiceCollection();
        AlbertoInstruments.Register(services);
        using var provider = services.BuildServiceProvider();

        Assert.Same(AlbertoMetrics.Shared, AlbertoInstruments.For(provider));
    }

    [Fact]
    public void Tenant_ownership_keeps_one_series_per_module_for_a_shared_consumer()
    {
        using var meter = new Meter("Alberto.Tests.Ownership");
        var instruments = new AlbertoInstruments(meter);

        instruments.RecordTenantOwnership("consumer-1", "orders", 3);
        instruments.RecordTenantOwnership("consumer-1", "payments", 5);

        var series = Observe<int>(meter, "alberto.owned_tenant_count");
        Assert.Equal(2, series.Count);
        Assert.Contains(series, s => s.Value == 3 && s.Tags["module"] as string == "orders");
        Assert.Contains(series, s => s.Value == 5 && s.Tags["module"] as string == "payments");
    }

    [Fact]
    public async Task A_stopped_head_drops_its_stall_series()
    {
        using var meter = new Meter("Alberto.Tests.HeadStalled");
        var instruments = new AlbertoInstruments(meter);
        instruments.RecordHeadStalled("orders", 42);
        var head = new EventStoreHead(
            new InMemoryEventStoreBackend(), refreshInterval: TimeSpan.FromMilliseconds(10),
            moduleKey: "orders", instruments: instruments);

        await head.StartAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        Assert.Empty(Observe<double>(meter, "alberto.head.stalled"));
    }

    private static List<(T Value, Dictionary<string, object?> Tags)> Observe<T>(Meter meter, string name) where T : struct
    {
        var series = new List<(T, Dictionary<string, object?>)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
            series.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();
        listener.RecordObservableInstruments();
        return series;
    }

    private sealed class PurgeOnlyStore : IOutboxStore
    {
        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(int limit, TimeSpan claimLease, string claimedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MarkAttemptFailedAsync(OutboxClaim claim, string error, int maxRetries, CancellationToken ct) => throw new NotSupportedException();
        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
