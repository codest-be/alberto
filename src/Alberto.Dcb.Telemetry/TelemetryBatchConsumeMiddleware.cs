using System.Diagnostics;
using Alberto.Dcb.Diagnostics;
using Alberto.Dcb.Subscriptions;

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
                activity?.SetTag("module.key", context.ModuleKey);
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
                        activity?.AddTag("exception.type", ex.GetType().FullName);
                        activity?.AddTag("exception.message", ex.Message);
                        activity?.AddTag("exception.stacktrace", ex.StackTrace);
                    }

                    AlbertoMetrics.ProcessingErrors.Add(
                        context.DeadLetteredCount,
                        new KeyValuePair<string, object?>("processor", context.ProcessorId),
                        new KeyValuePair<string, object?>("exception.type", ex?.GetType().Name ?? "Unknown"));
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    AlbertoMetrics.EventsProcessed.Add(
                        context.Envelopes.Count,
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

                AlbertoMetrics.ProcessingErrors.Add(
                    context.Envelopes.Count,
                    new KeyValuePair<string, object?>("processor", context.ProcessorId),
                    new KeyValuePair<string, object?>("exception.type", ex.GetType().Name));
                throw;
            }
            finally
            {
                activity?.Dispose();
                AlbertoMetrics.ProcessingDuration.Record(
                    sw.ElapsedMilliseconds,
                    new KeyValuePair<string, object?>("processor", context.ProcessorId));
            }
        };
    }
}
