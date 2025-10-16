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
        string eventType, bool isSynchronous)
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

        var activityName = $"{subscriptionName}:{eventType}";
        var appendActivityContext = new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded);

        Activity? activity;

        if (isSynchronous)
        {
            // Synchronous subscriptions (channels): Continue the parent trace as a child span
            activity = AlbertoActivitySource.Source.StartActivity(
                activityName,
                ActivityKind.Consumer,
                parentContext: appendActivityContext);
        }
        else
        {
            // Asynchronous subscriptions (polling): Create a linked trace for async processing
            var link = new ActivityLink(appendActivityContext);
            activity = AlbertoActivitySource.Source.StartActivity(
                activityName,
                ActivityKind.Consumer,
                parentContext: default,
                links: [link]);
        }

        if (activity == null)
        {
            return new NoopDisposable();
        }

        // Add telemetry tags for better observability
        activity.SetTag("subscription.name", subscriptionName);
        activity.SetTag("event.type", eventType);
        activity.SetTag("subscription.mode", isSynchronous ? "sync" : "async");

        logger.LogDebug(
            "Created {Mode} consumption trace context for {SubscriptionName}:{EventType} with trace {TraceId}",
            isSynchronous ? "synchronous" : "asynchronous",
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