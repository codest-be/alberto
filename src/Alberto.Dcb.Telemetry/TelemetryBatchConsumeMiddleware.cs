using System.Diagnostics;
using Alberto.Dcb.Diagnostics;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Builds a <see cref="BatchConsumeMiddleware"/> that adds OpenTelemetry instrumentation
/// to the batch consume pipeline.
/// </summary>
public static class TelemetryBatchConsumeMiddleware
{
    /// <summary>
    /// Creates a telemetry middleware for batch processing. When
    /// <paramref name="traceContextProvider"/> is supplied, the consumer span is
    /// linked to the trace that originally appended the first event in the batch.
    /// Trace-link extraction is handled by
    /// <see cref="TraceContextExtractor.ExtractTraceLink"/>, shared with
    /// <see cref="TelemetryConsumeMiddleware"/>.
    /// </summary>
    public static BatchConsumeMiddleware Create(ITraceContextProvider? traceContextProvider = null)
    {
        return async (context, next) =>
        {
            // Skip all trace-related allocations (string interpolation, link extraction,
            // ActivityLink array) when no ActivitySource listener is subscribed.
            // Metric counters and histograms are recorded unconditionally via the Meter.
            Activity? activity = null;
            if (AlbertoMetrics.Source.HasListeners())
            {
                var firstEnvelope = context.Envelopes[0];
                var link = TraceContextExtractor.ExtractTraceLink(firstEnvelope, traceContextProvider);
                var links = link is null ? null : new[] { link.Value };
                var activityName = $"{AlbertoMetrics.ConsumeActivityName} {context.ProcessorId} batch";
                activity = AlbertoMetrics.Source.StartActivity(
                    activityName,
                    ActivityKind.Consumer,
                    parentContext: default,
                    links: links);

                activity?.SetTag("processor.id", context.ProcessorId);
                activity?.SetTag("module", ShardKey.ModuleOf(context.ModuleKey));
                if (ShardKey.ShardOf(context.ModuleKey) is { } shardId)
                    activity?.SetTag("shard", shardId);
                activity?.SetTag("batch.size", context.Envelopes.Count);
                activity?.SetTag("event.position.first", firstEnvelope.GlobalPosition);
                activity?.SetTag("event.position.last", context.Envelopes[^1].GlobalPosition);
                activity?.SetTag("trace.links.count", links?.Length ?? 0);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await next();

                if (context.DeadLetteredCount > 0)
                {
                    var ex = context.LastError;
                    activity?.SetStatus(ActivityStatusCode.Error, ex?.Message ?? "Dead-lettered");
                    if (ex is not null)
                    {
                        // An exception EVENT, not span attributes. See the same call in
                        // TelemetryConsumeMiddleware for why messages must not become attributes.
                        activity?.AddException(ex);
                    }

                    // Use the same module/shard tag set as EventsProcessed so all three
                    // per-event metrics (processed, errors, duration) share the same label
                    // dimensions and can be correlated by a single join key in dashboards.
                    var deadLetterTags = TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey);
                    deadLetterTags.Add("exception.type", ex?.GetType().Name ?? "Unknown");
                    AlbertoMetrics.ProcessingErrors.Add(context.DeadLetteredCount, deadLetterTags);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    AlbertoMetrics.EventsProcessed.Add(
                        context.Envelopes.Count,
                        TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey));
                }
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);

                var catchTags = TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey);
                catchTags.Add("exception.type", ex.GetType().Name);
                AlbertoMetrics.ProcessingErrors.Add(context.Envelopes.Count, catchTags);
                throw;
            }
            finally
            {
                activity?.Dispose();
                // TotalSeconds matches the OTel semantic-convention unit "s" declared on ProcessingDuration.
                AlbertoMetrics.ProcessingDuration.Record(
                    sw.Elapsed.TotalSeconds,
                    TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey));
            }
        };
    }
}
