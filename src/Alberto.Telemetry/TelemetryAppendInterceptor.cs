using System.Diagnostics;
using Alberto.Tenancy;
using Alberto.Append;

namespace Alberto.Telemetry;

/// <summary>
/// Append interceptor that adds distributed tracing instrumentation.
/// - Creates an "append {module}" activity span
/// - Records events being appended as Activity events (showing what was decided)
/// </summary>
internal sealed class TelemetryAppendInterceptor(bool recordEventTagValues = false, string? moduleKey = null) : IAppendInterceptor
{
    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> OnAppendingAsync(
        AppendContext context,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> next,
        CancellationToken ct = default)
    {
        using var activity = AlbertoMetrics.Source.StartActivity(
            moduleKey is null
                ? AlbertoMetrics.AppendActivityName
                : $"{AlbertoMetrics.AppendActivityName} {ShardKey.ModuleOf(moduleKey)}",
            ActivityKind.Producer);

        if (activity is not null && moduleKey is not null)
        {
            activity.SetTag("alberto.module", ShardKey.ModuleOf(moduleKey));
            if (ShardKey.ShardOf(moduleKey) is { } shardId)
                activity.SetTag("alberto.shard", shardId);
        }

        activity?.SetTag("alberto.events.count", context.Events.Count);

        // Add each event being appended as an Activity event (shows "what was decided")
        foreach (var evt in context.Events)
        {
            activity?.AddEvent(new ActivityEvent(
                "alberto.event.appending",
                tags: new ActivityTagsCollection
                {
                    { "alberto.event.id", evt.Id.ToString() },
                    { "alberto.event.type", evt.EventType.Id },
                    { "alberto.event.tags", DescribeTags(evt.Tags) }
                }));
        }

        var tags = moduleKey is null ? new TagList() : ProcessorTags.ForModuleOnly(moduleKey);
        var outcome = "success";
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await next(context);

            // Record positions assigned
            if (result.Count > 0)
            {
                activity?.SetTag("alberto.events.first_position", result.First().GlobalPosition);
                activity?.SetTag("alberto.events.last_position", result.Last().GlobalPosition);
            }

            AlbertoMetrics.EventsAppended.Add(result.Count, tags);

            return result;
        }
        catch (DcbConflictException)
        {
            activity?.SetTag("error.type", typeof(DcbConflictException).FullName);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("alberto.dcb.conflict", true);

            outcome = "conflict";
            var conflictTags = tags;
            conflictTags.Add("kind", "append");
            AlbertoMetrics.ConcurrencyConflicts.Add(1, conflictTags);

            throw;
        }
        catch (Exception ex)
        {
            outcome = "error";
            TelemetrySpans.Fail(activity, ex);
            throw;
        }
        finally
        {
            tags.Add("outcome", outcome);
            AlbertoMetrics.AppendDuration.Record(sw.Elapsed.TotalSeconds, tags);
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
}
