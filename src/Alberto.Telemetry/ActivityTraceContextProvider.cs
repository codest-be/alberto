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
        var context = TraceContextPropagation.Extract(metadata);
        return context == default
            ? null
            : new TraceContext(TraceContextPropagation.FormatTraceParent(context), context.TraceState);
    }
}
