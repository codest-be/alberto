using System.Diagnostics;
using Alberto.Diagnostics;
using Alberto.Subscriptions;
using Alberto.Tenancy;

namespace Alberto.Telemetry;

/// <summary>
/// Builds a <see cref="BatchConsumeMiddleware"/> that adds OpenTelemetry instrumentation
/// to the batch consume pipeline.
/// </summary>
public static class TelemetryBatchConsumeMiddleware
{
    /// <summary>
    /// Creates a telemetry middleware for batch processing. When
    /// <paramref name="traceContextProvider"/> is supplied, the consumer span is
    /// linked to each distinct trace that appended an event in the batch (capped at
    /// <see cref="TraceContextExtractor.MaxBatchLinks"/>; <c>trace.links.dropped</c> counts the rest).
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
                // A batch has many upstream traces and a span only one parent, so it links to all
                // of them whatever the processor's TraceParenting says.
                var links = TraceContextExtractor.ExtractBatchLinks(
                    context.Envelopes, traceContextProvider, out var droppedLinks);
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
                activity?.SetTag("trace.links.count", links.Length);
                if (droppedLinks > 0)
                    activity?.SetTag("trace.links.dropped", droppedLinks);
            }

            var ageTags = TelemetryTags.ForModule(context.ProcessorId, context.ModuleKey);
            var now = DateTimeOffset.UtcNow;
            foreach (var envelope in context.Envelopes)
                AlbertoMetrics.EventAge.Record((now - envelope.CreatedAt).TotalSeconds, ageTags);

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
