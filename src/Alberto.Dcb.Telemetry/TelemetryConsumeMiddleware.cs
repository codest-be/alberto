using System.Diagnostics;
using Alberto.Dcb.Diagnostics;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;

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
    /// appended the event. Trace-link extraction is handled by
    /// <see cref="TraceContextExtractor.ExtractTraceLink"/>, shared with
    /// <see cref="TelemetryBatchConsumeMiddleware"/>.
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
                var link = TraceContextExtractor.ExtractTraceLink(context.Envelope, traceContextProvider);
                var links = link is null ? null : new[] { link.Value };
                var activityName = $"{AlbertoMetrics.ConsumeActivityName} {context.ProcessorId}";
                activity = AlbertoMetrics.Source.StartActivity(
                    activityName,
                    ActivityKind.Consumer,
                    parentContext: default,
                    links: links);

                activity?.SetTag("processor.id", context.ProcessorId);
                activity?.SetTag("module", ShardKey.ModuleOf(context.ModuleKey));
                if (ShardKey.ShardOf(context.ModuleKey) is { } shardId)
                    activity?.SetTag("shard", shardId);
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
                        // An exception EVENT, not span attributes. Exception messages carry
                        // whatever the thrower put in them — Npgsql's include the failing SQL —
                        // so they belong where collectors already know to find and scrub them.
                        activity?.AddException(ex);
                    }

                    // Use the same module/shard tag set as EventsProcessed so all three
                    // per-event metrics (processed, errors, duration) share the same label
                    // dimensions and can be correlated by a single join key in dashboards.
                    var deadLetterTags = TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey);
                    deadLetterTags.Add("exception.type", ex?.GetType().Name ?? "Unknown");
                    AlbertoMetrics.ProcessingErrors.Add(1, deadLetterTags);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    AlbertoMetrics.EventsProcessed.Add(
                        1, TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey));
                }
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);

                var catchTags = TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey);
                catchTags.Add("exception.type", ex.GetType().Name);
                AlbertoMetrics.ProcessingErrors.Add(1, catchTags);
                throw;
            }
            finally
            {
                activity?.Dispose();
                // TotalSeconds matches the OTel semantic-convention unit "s" declared on ProcessingDuration.
                AlbertoMetrics.ProcessingDuration.Record(sw.Elapsed.TotalSeconds,
                    TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey));
            }
        };
    }
}
