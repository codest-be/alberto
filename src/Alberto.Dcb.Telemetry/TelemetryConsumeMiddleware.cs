using System.Diagnostics;
using Alberto.Dcb.Diagnostics;
using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Builds a <see cref="ConsumeMiddleware"/> that adds OpenTelemetry instrumentation
/// to the consume pipeline. Records traces, metrics and timing for each processed
/// event, and links the consumer span to the original append trace when trace
/// context is present in the event metadata.
/// </summary>
public static class TelemetryConsumeMiddleware
{
    /// <summary>
    /// Creates a telemetry middleware. When <paramref name="traceContextProvider"/>
    /// is supplied, the consumer span is linked to the trace that originally
    /// appended the event.
    /// </summary>
    public static ConsumeMiddleware Create(ITraceContextProvider? traceContextProvider = null)
    {
        return async (context, next) =>
        {
            // Skip all trace-related allocations (string interpolation, link extraction,
            // ActivityLink array) when no ActivitySource listener is subscribed.
            // Metric counters and histograms are recorded unconditionally via the Meter.
            Activity? activity = null;
            if (AlbertoMetrics.Source.HasListeners())
            {
                var link = ExtractTraceLink(context.Envelope, traceContextProvider);
                var links = link is null ? null : new[] { link.Value };
                var activityName = $"{AlbertoMetrics.ConsumeActivityName} {context.ProcessorId}";
                activity = AlbertoMetrics.Source.StartActivity(
                    activityName,
                    ActivityKind.Consumer,
                    parentContext: default,
                    links: links);

                activity?.SetTag("processor.id", context.ProcessorId);
                activity?.SetTag("module.key", context.ModuleKey);
                activity?.SetTag("event.position", context.Envelope.GlobalPosition);
                activity?.SetTag("event.type", context.Envelope.EventType.Id);
                activity?.SetTag("trace.links.count", links?.Length ?? 0);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await next();

                if (context.DeadLettered)
                {
                    var ex = context.LastError;
                    activity?.SetStatus(ActivityStatusCode.Error, ex?.Message ?? "Dead-lettered");
                    if (ex is not null)
                    {
                        activity?.AddTag("exception.type", ex.GetType().FullName);
                        activity?.AddTag("exception.message", ex.Message);
                        activity?.AddTag("exception.stacktrace", ex.StackTrace);
                    }

                    AlbertoMetrics.ProcessingErrors.Add(1,
                        new KeyValuePair<string, object?>("processor", context.ProcessorId),
                        new KeyValuePair<string, object?>("exception.type",
                            ex?.GetType().Name ?? "Unknown"));
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    AlbertoMetrics.EventsProcessed.Add(1,
                        new KeyValuePair<string, object?>("processor", context.ProcessorId),
                        new KeyValuePair<string, object?>("module", context.ModuleKey));
                }
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
                activity?.Dispose();
                AlbertoMetrics.ProcessingDuration.Record(sw.ElapsedMilliseconds,
                    new KeyValuePair<string, object?>("processor", context.ProcessorId));
            }
        };
    }

    private static ActivityLink? ExtractTraceLink(IEventEnvelope envelope, ITraceContextProvider? provider)
    {
        if (provider is null)
            return null;

        var traceContext = provider.ExtractTraceContext(envelope.Metadata);
        if (traceContext is null)
            return null;

        // W3C traceparent format: 00-{traceId}-{spanId}-01
        if (!ActivityContext.TryParse(
            $"00-{traceContext.TraceId}-{traceContext.SpanId}-01",
            null,
            out var activityContext))
            return null;

        return new ActivityLink(activityContext, new ActivityTagsCollection
        {
            { "event.position", envelope.GlobalPosition }
        });
    }
}
