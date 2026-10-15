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
        Assert.Equal(0, activity.GetTagItem("trace.links.count"));
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
        Assert.Equal(2, activity.GetTagItem("trace.links.count"));
        Assert.Null(activity.GetTagItem("trace.links.dropped"));
        Assert.Equal(default, activity.ParentSpanId);
    }

    [Fact]
    public async Task Batch_caps_links_and_reports_how_many_traces_were_dropped()
    {
        var max = TraceContextExtractor.MaxBatchLinks;
        var activity = await ConsumeBatchAsync(
            Enumerable.Range(1, max + 5).Select(n => (string?)TraceParent(n)).ToArray());

        Assert.Equal(max, activity.Links.Count());
        Assert.Equal(max, activity.GetTagItem("trace.links.count"));
        Assert.Equal(5, activity.GetTagItem("trace.links.dropped"));
    }
}
