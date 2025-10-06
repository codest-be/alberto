using System.Diagnostics;
using Alberto.EventStore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Telemetry;

/// <summary>
/// OpenTelemetry implementation of trace context provider for subscription tracing
/// </summary>
public sealed class ActivityTraceContextProvider(
    ILogger<ActivityTraceContextProvider> logger) : ITraceContextProvider
{
    public IDisposable CreateScopeFromMetadata(IReadOnlyDictionary<string, string> metadata, string subscriptionName,
        string eventType)
    {
        // Extract trace information from event metadata
        if (!metadata.TryGetValue("_traceId", out var traceIdString) ||
            !metadata.TryGetValue("_spanId", out var parentSpanIdString))
        {
            // No trace information in metadata, return no-op scope
            return new NoopDisposable();
        }

        // Parse the trace and span IDs
        ActivityTraceId traceId;
        ActivitySpanId parentSpanId;

        try
        {
            traceId = ActivityTraceId.CreateFromString(traceIdString.AsSpan());
            parentSpanId = ActivitySpanId.CreateFromString(parentSpanIdString.AsSpan());
        }
        catch (ArgumentException)
        {
            logger.LogWarning(
                "Invalid trace or span ID in event metadata. TraceId: {TraceId}, SpanId: {SpanId}",
                traceIdString,
                parentSpanIdString);
            return new NoopDisposable();
        }

        // Create a new activity that links to the original append activity
        var activityName = $"{subscriptionName}:{eventType}";
        var appendActivityContext = new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded);
        var link = new ActivityLink(appendActivityContext);

        var activity = AlbertoActivitySource.Source.StartActivity(
            activityName,
            ActivityKind.Consumer,
            parentContext: default,
            links: [link]);

        if (activity == null)
        {
            return new NoopDisposable();
        }

        // Add telemetry tags for better observability
        activity.SetTag("subscription.name", subscriptionName);
        activity.SetTag("event.type", eventType);

        logger.LogDebug(
            "Created consumption trace context for {SubscriptionName}:{EventType} with trace {TraceId}",
            subscriptionName,
            eventType,
            activity.TraceId);

        return new ActivityScope(activity);
    }

    private sealed class ActivityScope(Activity activity) : IDisposable
    {
        public void Dispose()
        {
            activity.Dispose();
        }
    }
}