using System.Diagnostics;
using Alberto.Dcb;
using Alberto.Dcb.Diagnostics;
using Alberto.Dcb.Subscriptions.Pipeline;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Consume filter that adds OpenTelemetry instrumentation.
/// Links consumer activities to original append traces when trace context is present in metadata.
/// Records traces, metrics, and timing for event processing.
/// </summary>
#pragma warning disable CS0618
internal sealed class TelemetryConsumeFilter : IConsumeFilter
#pragma warning restore CS0618
{
    private readonly ITraceContextProvider? _traceContextProvider;

    /// <summary>
    /// Creates a new telemetry consume filter without trace linking.
    /// </summary>
    public TelemetryConsumeFilter() : this(null)
    {
    }

    /// <summary>
    /// Creates a new telemetry consume filter with trace context extraction for linking.
    /// </summary>
    /// <param name="traceContextProvider">Provider for extracting trace context from event metadata.</param>
    public TelemetryConsumeFilter(ITraceContextProvider? traceContextProvider)
    {
        _traceContextProvider = traceContextProvider;
    }

    /// <inheritdoc />
    public async Task OnConsumingAsync(
        IReadOnlyList<IEventEnvelope> events,
        ConsumeContext context,
        Func<Task> next,
        CancellationToken ct = default)
    {
        // Extract trace links from event metadata
        var links = ExtractTraceLinks(events);

        // Include processor name in span for easy identification
        var activityName = $"{AlbertoMetrics.ConsumeActivityName} {context.ProcessorId}";

        using var activity = AlbertoMetrics.Source.StartActivity(
            activityName,
            ActivityKind.Consumer,
            parentContext: default,
            links: links);

        activity?.SetTag("processor.id", context.ProcessorId);
        activity?.SetTag("module.key", context.ModuleKey);
        activity?.SetTag("events.count", events.Count);
        activity?.SetTag("trace.links.count", links.Count);

        if (events.Count > 0)
        {
            activity?.SetTag("events.first_position", events[0].GlobalPosition);
            activity?.SetTag("events.last_position", events[^1].GlobalPosition);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await next();

            activity?.SetStatus(ActivityStatusCode.Ok);
            AlbertoMetrics.EventsProcessed.Add(events.Count,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("module", context.ModuleKey));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
            activity?.AddTag("exception.stacktrace", ex.StackTrace);

            AlbertoMetrics.ProcessingErrors.Add(1,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("exception.type", ex.GetType().Name));

            throw;
        }
        finally
        {
            AlbertoMetrics.ProcessingDuration.Record(sw.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("processor", context.ProcessorId));
        }
    }

    private IReadOnlyList<ActivityLink> ExtractTraceLinks(IReadOnlyList<IEventEnvelope> events)
    {
        if (_traceContextProvider is null)
            return [];

        var links = new List<ActivityLink>();
        var seenTraces = new HashSet<string>();

        foreach (var evt in events)
        {
            var traceContext = _traceContextProvider.ExtractTraceContext(evt.Metadata);
            if (traceContext is null)
                continue;

            // Deduplicate links by trace+span combination
            var key = $"{traceContext.TraceId}:{traceContext.SpanId}";
            if (!seenTraces.Add(key))
                continue;

            // Parse into ActivityContext for linking
            // Format: "00-{traceId}-{spanId}-01" (W3C traceparent format)
            if (ActivityContext.TryParse(
                $"00-{traceContext.TraceId}-{traceContext.SpanId}-01",
                null,
                out var activityContext))
            {
                links.Add(new ActivityLink(activityContext, new ActivityTagsCollection
                {
                    { "event.position", evt.GlobalPosition }
                }));
            }
        }

        return links;
    }
}
