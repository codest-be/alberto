using System.Diagnostics;
using Alberto.Diagnostics;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using Alberto.Configuration;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// <see cref="TraceParenting"/> decides whether a consume span is a child of the appending trace or a
/// fresh root linking to it; batches always link, one link per distinct upstream trace.
/// </summary>
[Collection(OtelTracingCollection.Name)]
public sealed class ConsumeParentingTests
{
    private static readonly ITraceContextProvider Provider = new ActivityTraceContextProvider();

    private static string TraceParent(int n, bool sampled = true) =>
        $"00-{n:x32}-{n:x16}-{(sampled ? "01" : "00")}";

    private static EventEnvelope Envelope(long position, string? traceParent) => new()
    {
        Id = Guid.NewGuid(),
        GlobalPosition = position,
        EventType = new EventType("test-event"),
        Tags = [],
        EventData = "{}",
        Metadata = traceParent is null ? [] : new Dictionary<string, string> { ["traceparent"] = traceParent },
        CreatedAt = DateTime.UtcNow,
    };

    private static (ActivityListener Listener, List<Activity> Stopped) Listen()
    {
        var stopped = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Alberto",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, stopped);
    }

    private static async Task<Activity> ConsumeAsync(TraceParenting parenting, string? traceParent)
    {
        var (listener, stopped) = Listen();
        using (listener)
        {
            var context = new ConsumeEventContext
            {
                ProcessorId = "parenting-proc",
                ModuleKey = "orders",
                Envelope = Envelope(7, traceParent),
                IsRebuild = false,
                TraceParenting = parenting,
            };
            await TelemetryConsumeMiddleware.Create(Provider)(context, () => Task.CompletedTask);
            return stopped.Single(a => a.OperationName.Contains("parenting-proc"));
        }
    }

    [Fact]
    public async Task Parent_makes_the_append_trace_the_parent_and_adds_no_link()
    {
        var activity = await ConsumeAsync(TraceParenting.Parent, TraceParent(0xabc));

        Assert.Equal(ActivityTraceId.CreateFromString(TraceParent(0xabc).AsSpan(3, 32)), activity.TraceId);
        Assert.Equal(ActivitySpanId.CreateFromString(TraceParent(0xabc).AsSpan(36, 16)), activity.ParentSpanId);
        Assert.Empty(activity.Links);
        Assert.Equal(0, activity.GetTagItem("alberto.trace.links.count"));
    }

    [Fact]
    public async Task Link_starts_a_new_root_trace_with_a_link_to_the_append_trace()
    {
        var activity = await ConsumeAsync(TraceParenting.Link, TraceParent(0xabc));

        Assert.NotEqual(ActivityTraceId.CreateFromString(TraceParent(0xabc).AsSpan(3, 32)), activity.TraceId);
        Assert.Equal(default, activity.ParentSpanId);
        Assert.Equal(TraceParent(0xabc).Substring(3, 32), activity.Links.Single().Context.TraceId.ToHexString());
    }

    [Fact]
    public async Task Parent_without_upstream_context_falls_back_to_a_root_span()
    {
        var activity = await ConsumeAsync(TraceParenting.Parent, traceParent: null);

        Assert.Equal(default, activity.ParentSpanId);
        Assert.Empty(activity.Links);
    }

    private static readonly ActivitySource Foreign = new("Alberto.Tests.Ambient");

    [Fact]
    public async Task Link_and_batch_are_true_roots_even_under_an_ambient_activity()
    {
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Alberto.Tests.Ambient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(ambientListener);
        using var ambient = Foreign.StartActivity("append-path")!;

        var link = await ConsumeAsync(TraceParenting.Link, TraceParent(0xabc));
        var noContext = await ConsumeAsync(TraceParenting.Parent, traceParent: null);
        var batch = await ConsumeBatchAsync(TraceParent(1));

        foreach (var span in new[] { link, noContext, batch })
        {
            Assert.NotEqual(ambient.TraceId, span.TraceId);
            Assert.Equal(default, span.ParentSpanId);
        }
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task Event_age_is_not_recorded_while_rebuilding()
    {
        var ages = new List<string?>();
        using var meter = new System.Diagnostics.Metrics.MeterListener();
        meter.InstrumentPublished = (i, l) =>
        {
            if (i.Name == "alberto.event.age") l.EnableMeasurementEvents(i);
        };
        meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "processor") lock (ages) ages.Add((string?)t.Value);
        });
        meter.Start();

        foreach (var rebuild in new[] { true, false })
        {
            await TelemetryConsumeMiddleware.Create()(
                new ConsumeEventContext
                {
                    ProcessorId = rebuild ? "age-rebuild" : "age-live",
                    ModuleKey = "orders",
                    Envelope = Envelope(1, null),
                    IsRebuild = rebuild,
                },
                () => Task.CompletedTask);
            await TelemetryBatchConsumeMiddleware.Create()(
                new BatchConsumeContext
                {
                    ProcessorId = rebuild ? "age-rebuild" : "age-live",
                    ModuleKey = "orders",
                    Envelopes = [Envelope(1, null)],
                    IsRebuild = rebuild,
                },
                () => Task.CompletedTask);
        }

        lock (ages)
        {
            Assert.DoesNotContain("age-rebuild", ages);
            Assert.Equal(2, ages.Count(a => a == "age-live"));
        }
    }

    [Fact]
    public void Defaults_reactors_and_outbox_parent_projections_link()
    {
        Assert.Equal(TraceParenting.Link, ProcessorExecutionOptions.Default.TraceParenting);
        Assert.Equal(
            TraceParenting.Parent,
            new ProcessorExecutionOverrides().ApplyTo(
                ProcessorExecutionOptions.Default with { TraceParenting = TraceParenting.Parent }).TraceParenting);
        Assert.Equal(
            TraceParenting.Link,
            new ProcessorExecutionOverrides { TraceParenting = TraceParenting.Link }.ApplyTo(
                ProcessorExecutionOptions.Default with { TraceParenting = TraceParenting.Parent }).TraceParenting);
    }

    private static async Task<Activity> ConsumeBatchAsync(params string?[] traceParents)
    {
        var (listener, stopped) = Listen();
        using (listener)
        {
            var context = new BatchConsumeContext
            {
                ProcessorId = "parenting-batch",
                ModuleKey = "orders",
                Envelopes = traceParents.Select((tp, i) => (IEventEnvelope)Envelope(i + 1, tp)).ToArray(),
                IsRebuild = false,
            };
            await TelemetryBatchConsumeMiddleware.Create(Provider)(context, () => Task.CompletedTask);
            return stopped.Single(a => a.OperationName.Contains("parenting-batch"));
        }
    }

    [Fact]
    public async Task Batch_links_every_distinct_upstream_trace_once()
    {
        // Events 1 and 3 share a trace; event 2 has none; event 4 is a different trace.
        var activity = await ConsumeBatchAsync(TraceParent(1), null, TraceParent(1), TraceParent(2));

        Assert.Equal(2, activity.Links.Count());
        Assert.Equal(2, activity.GetTagItem("alberto.trace.links.count"));
        Assert.Null(activity.GetTagItem("alberto.trace.links.dropped"));
        Assert.Equal(default, activity.ParentSpanId);
    }

    [Fact]
    public async Task Batch_caps_links_and_reports_how_many_traces_were_dropped()
    {
        var max = TraceContextExtractor.MaxBatchLinks;
        var activity = await ConsumeBatchAsync(
            Enumerable.Range(1, max + 5).Select(n => (string?)TraceParent(n)).ToArray());

        Assert.Equal(max, activity.Links.Count());
        Assert.Equal(max, activity.GetTagItem("alberto.trace.links.count"));
        Assert.Equal(5, activity.GetTagItem("alberto.trace.links.dropped"));
    }
}
