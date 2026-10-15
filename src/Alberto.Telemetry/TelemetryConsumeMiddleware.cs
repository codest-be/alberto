using System.Diagnostics;
using Alberto.Diagnostics;
using Alberto.Subscriptions;
using Alberto.Tenancy;

namespace Alberto.Telemetry;

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
    /// appended the event. Trace-link extraction is handled by
    /// <see cref="TraceContextExtractor.ExtractTraceLink"/>, shared with
    /// <see cref="TelemetryBatchConsumeMiddleware"/>. With <see cref="TraceParenting.Parent"/>
    /// on the context the appending trace is the span's parent instead of a link.
    /// </summary>
    public static ConsumeMiddleware Create(ITraceContextProvider? traceContextProvider = null)
    {
        return async (context, next) =>
        {
            // Skip all trace-related allocations (string interpolation, link extraction,
            // ActivityLink array) when no ActivitySource listener is subscribed.
            // Metric counters and histograms are recorded unconditionally via the Meter.
            Activity? activity = null;
            var ambient = Activity.Current;
            if (AlbertoMetrics.Source.HasListeners())
            {
                // Parent: the append trace becomes this span's parent, so the flow is one trace and
                // head sampling decides it once. Link: a fresh root that points back at it.
                var parent = context.TraceParenting == TraceParenting.Parent
                    ? TraceContextExtractor.ExtractContext(context.Envelope, traceContextProvider)
                    : null;
                var link = parent is null
                    ? TraceContextExtractor.ExtractTraceLink(context.Envelope, traceContextProvider)
                    : null;
                var links = link is null ? null : new[] { link.Value };
                var activityName = $"{AlbertoMetrics.ConsumeActivityName} {context.ProcessorId}";
                activity = TelemetrySpans.StartConsumer(activityName, parent ?? default, links);

                TelemetrySpans.TagConsumer(activity, context.ProcessorId, context.ModuleKey);
                activity?.SetTag("alberto.event.position", context.Envelope.GlobalPosition);
                activity?.SetTag("alberto.event.type", context.Envelope.EventType.Id);
                activity?.SetTag("alberto.trace.links.count", links?.Length ?? 0);
            }

            // A rebuild or replay re-reads history; its ages would swamp the real delay readers see.
            if (!context.IsRebuild)
                AlbertoMetrics.EventAge.Record(
                    (DateTimeOffset.UtcNow - context.Envelope.CreatedAt).TotalSeconds,
                    TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey));

            var sw = Stopwatch.StartNew();
            try
            {
                await next();

                if (context.DeadLettered)
                {
                    var ex = context.LastError;
                    // No exception message on the span: it carries whatever the thrower put in
                    // it, and Npgsql's include the failing SQL. See TelemetrySpans.Fail.
                    TelemetrySpans.Fail(activity, ex);

                    // Use the same module/shard tag set as EventsProcessed so all three
                    // per-event metrics (processed, errors, duration) share the same label
                    // dimensions and can be correlated by a single join key in dashboards.
                    var deadLetterTags = TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey);
                    deadLetterTags.Add("error.type", ex?.GetType().FullName ?? "dead_lettered");
                    AlbertoMetrics.ProcessingErrors.Add(1, deadLetterTags);
                }
                else
                {
                    AlbertoMetrics.EventsProcessed.Add(
                        1, TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey));
                }
            }
            catch (Exception ex)
            {
                TelemetrySpans.Fail(activity, ex);

                var catchTags = TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey);
                catchTags.Add("error.type", ex.GetType().FullName);
                AlbertoMetrics.ProcessingErrors.Add(1, catchTags);
                throw;
            }
            finally
            {
                activity?.Dispose();
                Activity.Current = ambient;
                // TotalSeconds matches the OTel semantic-convention unit "s" declared on ProcessingDuration.
                AlbertoMetrics.ProcessingDuration.Record(sw.Elapsed.TotalSeconds,
                    TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey));
            }
        };
    }
}
