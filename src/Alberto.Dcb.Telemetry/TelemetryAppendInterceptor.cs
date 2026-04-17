using System.Diagnostics;
using Alberto.Dcb.Append;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Append interceptor that adds distributed tracing instrumentation.
/// - Stores trace context (_traceId, _spanId) in event metadata
/// - Creates "Alberto.Append" activity span
/// - Records events being appended as Activity events (showing what was decided)
/// </summary>
internal sealed class TelemetryAppendInterceptor : IAppendInterceptor
{
    /// <summary>
    /// Metadata key for the trace ID.
    /// </summary>
    public const string TraceIdKey = "_traceId";

    /// <summary>
    /// Metadata key for the span ID.
    /// </summary>
    public const string SpanIdKey = "_spanId";

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> OnAppendingAsync(
        AppendContext context,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> next,
        CancellationToken ct = default)
    {
        using var activity = AlbertoMetrics.Source.StartActivity(
            AlbertoMetrics.AppendActivityName,
            ActivityKind.Producer);

        activity?.SetTag("events.count", context.Events.Count);

        // Enrich events with trace context
        var enrichedEvents = EnrichEventsWithTraceContext(context.Events, activity);
        var enrichedContext = context.WithEvents(enrichedEvents);

        // Add each event being appended as an Activity event (shows "what was decided")
        foreach (var evt in context.Events)
        {
            activity?.AddEvent(new ActivityEvent(
                "event.appending",
                tags: new ActivityTagsCollection
                {
                    { "event.id", evt.Id.ToString() },
                    { "event.type", evt.EventType.Id },
                    { "event.tags", string.Join(",", evt.Tags.Select(t => t.Value)) }
                }));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await next(enrichedContext);

            activity?.SetStatus(ActivityStatusCode.Ok);

            // Record positions assigned
            if (result.Count > 0)
            {
                activity?.SetTag("events.first_position", result.First().GlobalPosition);
                activity?.SetTag("events.last_position", result.Last().GlobalPosition);
            }

            AlbertoMetrics.EventsAppended.Add(result.Count);

            return result;
        }
        catch (DcbConflictException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "DCB conflict");
            activity?.SetTag("dcb.conflict", true);

            AlbertoMetrics.ConcurrencyConflicts.Add(1);

            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            throw;
        }
        finally
        {
            AlbertoMetrics.AppendDuration.Record(sw.ElapsedMilliseconds);
        }
    }

    private static IReadOnlyList<IEventToPersist> EnrichEventsWithTraceContext(
        IReadOnlyList<IEventToPersist> events,
        Activity? activity)
    {
        if (activity is null)
            return events;

        var traceId = activity.TraceId.ToString();
        var spanId = activity.SpanId.ToString();

        return events.Select(evt =>
        {
            var enrichedMetadata = new Dictionary<string, string>(evt.Metadata)
            {
                [TraceIdKey] = traceId,
                [SpanIdKey] = spanId
            };

            return new EventToPersist
            {
                Id = evt.Id,
                EventType = evt.EventType,
                Tags = evt.Tags,
                EventData = evt.EventData,
                Metadata = enrichedMetadata
            };
        }).ToList();
    }
}
