using System.Diagnostics;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.EventStore.Telemetry;
using Alberto.Projections;

namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Accumulates projection updates for batching.
/// Used internally by ProjectionHandler when a batch scope is active.
/// </summary>
internal sealed class ProjectionBatchAccumulator<TKey, TState>(
    IProjectionRepository<TKey, TState> repository,
    IProjector<TState> projector)
    where TKey : notnull
    where TState : new()
{
    private readonly Dictionary<TKey, List<(object Event, EventContext Context)>> _eventsByKey = new();

    public void Add(TKey key, object @event, EventContext context)
    {
        if (!_eventsByKey.TryGetValue(key, out var events))
        {
            events = new List<(object, EventContext)>();
            _eventsByKey[key] = events;
        }

        events.Add((@event, context));
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        if (_eventsByKey.Count == 0)
            return;

        // Extract activity links from all events in the batch
        var activityLinks = ExtractActivityLinks();

        // Calculate total event count
        var totalEvents = _eventsByKey.Values.Sum(list => list.Count);

        // Create batch projection activity with links to all producer events
        using var activity = AlbertoActivitySource.Source.StartActivity(
            $"projection.batch.{typeof(TState).Name}",
            ActivityKind.Consumer,
            parentContext: default, // No single parent - batch operation
            links: activityLinks);

        activity?.SetTag("projection.type", typeof(TState).Name);
        activity?.SetTag("projection.batch_size", totalEvents);
        activity?.SetTag("projection.keys_count", _eventsByKey.Count);

        var startTime = DateTime.UtcNow;

        try
        {
            // Batch load all current states in one query
            var currentStates = await repository.BatchGet(_eventsByKey.Keys, cancellationToken);

            // Build updates dictionary
            var updates = new Dictionary<TKey, (TState State, long Version)>();

            foreach (var (key, events) in _eventsByKey)
            {
                // Get current state from batch-loaded results
                var currentState = currentStates.TryGetValue(key, out var existing) && existing != null
                    ? existing
                    : new TState();

                // Fold all events for this key (functional!)
                var newState = events.Aggregate(
                    currentState,
                    (state, tuple) => projector.Apply(state, tuple.Event)
                );

                // Track highest version
                var maxVersion = events.Max(e => e.Context.GlobalPosition);

                updates[key] = (newState, maxVersion);
            }

            // Batch save
            var rowsAffected = await repository.BatchUpsertWithVersion(updates, cancellationToken);

            activity?.SetTag("projection.keys_updated", rowsAffected);
            activity?.SetStatus(ActivityStatusCode.Ok);

            // Record metrics (if available)
            RecordBatchMetrics(totalEvents, rowsAffected, startTime);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            activity?.SetTag("exception.stacktrace", ex.StackTrace);
            throw;
        }
    }

    private IEnumerable<ActivityLink> ExtractActivityLinks()
    {
        var links = new List<ActivityLink>();

        foreach (var events in _eventsByKey.Values)
        {
            foreach (var (_, context) in events)
            {
                var link = ExtractActivityLink(context.Metadata);
                if (link.HasValue)
                {
                    links.Add(link.Value);
                }
            }
        }

        return links;
    }

    private static ActivityLink? ExtractActivityLink(IReadOnlyDictionary<string, string> metadata)
    {
        // Extract trace information from event metadata (same format as ActivityTraceContextProvider)
        if (!metadata.TryGetValue("_traceId", out var traceIdString) ||
            !metadata.TryGetValue("_spanId", out var parentSpanIdString))
        {
            return null;
        }

        // Parse the trace and span IDs
        try
        {
            var traceId = ActivityTraceId.CreateFromString(traceIdString.AsSpan());
            var parentSpanId = ActivitySpanId.CreateFromString(parentSpanIdString.AsSpan());
            var activityContext = new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded);

            return new ActivityLink(activityContext);
        }
        catch (ArgumentException)
        {
            // Invalid trace or span ID, skip this link
            return null;
        }
    }

    private static void RecordBatchMetrics(int batchSize, int keysUpdated, DateTime startTime)
    {
        try
        {
            // Try to access metrics (Telemetry assembly might not be available)
            var metricsType = Type.GetType("Alberto.EventStore.Telemetry.AlbertoMeter, Alberto.EventStore.Telemetry");
            if (metricsType == null) return;

            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

            metricsType.GetProperty("ProjectionBatchesCommitted")?.GetValue(null)
                ?.GetType().GetMethod("Add")?.Invoke(
                    metricsType.GetProperty("ProjectionBatchesCommitted")?.GetValue(null),
                    new object[] { 1L, Array.Empty<KeyValuePair<string, object?>>() });

            metricsType.GetProperty("ProjectionBatchSize")?.GetValue(null)
                ?.GetType().GetMethod("Record")?.Invoke(
                    metricsType.GetProperty("ProjectionBatchSize")?.GetValue(null),
                    new object[] { batchSize, Array.Empty<KeyValuePair<string, object?>>() });

            metricsType.GetProperty("ProjectionBatchDuration")?.GetValue(null)
                ?.GetType().GetMethod("Record")?.Invoke(
                    metricsType.GetProperty("ProjectionBatchDuration")?.GetValue(null),
                    new object[] { duration, Array.Empty<KeyValuePair<string, object?>>() });

            metricsType.GetProperty("ProjectionBatchKeysUpdated")?.GetValue(null)
                ?.GetType().GetMethod("Record")?.Invoke(
                    metricsType.GetProperty("ProjectionBatchKeysUpdated")?.GetValue(null),
                    new object[] { keysUpdated, Array.Empty<KeyValuePair<string, object?>>() });
        }
        catch
        {
            // Silently ignore if metrics are not available
        }
    }
}