using Alberto.Diagnostics;

namespace Alberto.Telemetry;

/// <summary>
/// Extracts W3C trace context from event metadata.
/// </summary>
internal sealed class ActivityTraceContextProvider : ITraceContextProvider
{
    /// <inheritdoc />
    public TraceContext? ExtractTraceContext(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue(TelemetryAppendInterceptor.TraceIdKey, out var traceId) &&
            metadata.TryGetValue(TelemetryAppendInterceptor.SpanIdKey, out var spanId))
        {
            return new TraceContext(traceId, spanId);
        }

        return null;
    }
}
