using System.Diagnostics;
using Alberto.Append;

namespace Alberto.Telemetry;

/// <summary>
/// Append interceptor that adds distributed tracing instrumentation.
/// - Stores trace context (_traceId, _spanId) in event metadata
/// - Creates "Alberto.Append" activity span
/// - Records events being appended as Activity events (showing what was decided)
/// </summary>
internal sealed class TelemetryAppendInterceptor(bool recordEventTagValues = false) : IAppendInterceptor
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
                    { "event.tags", DescribeTags(evt.Tags) }
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
            // An exception EVENT, not span attributes. See the same call in
            // TelemetryConsumeMiddleware for why messages must not become attributes.
            activity?.AddException(ex);
            throw;
        }
        finally
        {
            AlbertoMetrics.AppendDuration.Record(sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Renders an append's tags for the span event.
    /// </summary>
    /// <remarks>
    /// By default only the concepts are emitted — <c>order,customer</c> rather than
    /// <c>order:8f21,customer:4471</c>. The concept is what identifies the consistency boundary
    /// the append was checked against, which is the part that explains the span; the id is a
    /// business identifier and is withheld unless
    /// <see cref="Configuration.TelemetryOptions.RecordEventTagValues"/> says otherwise.
    /// Concepts are deduplicated because an event tagged with several ids of one concept would
    /// otherwise repeat that concept without adding anything.
    /// </remarks>
    private string DescribeTags(IReadOnlyCollection<EventTag> tags) =>
        recordEventTagValues
            ? string.Join(",", tags.Select(t => t.Value))
            : string.Join(",", tags.Select(t => t.Concept).Distinct(StringComparer.Ordinal));

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
