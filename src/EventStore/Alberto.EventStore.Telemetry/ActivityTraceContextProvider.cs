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
    public IDisposable CreateScopeFromMetadata(IReadOnlyDictionary<string, string> metadata)
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

        // Create a new activity that links to the original trace
        var activity = AlbertoActivitySource.Source.StartActivity(
            "consume-event",
            ActivityKind.Consumer,
            new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded));

        if (activity == null)
        {
            return new NoopDisposable();
        }

        logger.LogDebug(
            "Created consumption trace context with trace {TraceId}",
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