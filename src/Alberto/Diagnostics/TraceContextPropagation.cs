using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Alberto.Diagnostics;

/// <summary>
/// Writes and reads W3C trace context in event and outbox metadata, so a trace survives the
/// asynchronous hops between an append, its consumers and the messages relayed from it.
/// </summary>
/// <remarks>
/// Goes through <see cref="Propagators.DefaultTextMapPropagator"/> rather than formatting
/// <c>traceparent</c> by hand, so the sampled flag, <c>tracestate</c> and (when configured)
/// baggage are carried exactly as the application's propagator defines them. That propagator is a
/// no-op until the OpenTelemetry SDK is initialised, so the W3C keys are read and written directly
/// as well: context must flow whether or not an exporter is configured.
/// </remarks>
internal static class TraceContextPropagation
{
    internal const string TraceParentKey = "traceparent";
    internal const string TraceStateKey = "tracestate";
    internal const string BaggageKey = "baggage";

    // Events stored before W3C keys were adopted carry these instead.
    internal const string LegacyTraceIdKey = "_traceId";
    internal const string LegacySpanIdKey = "_spanId";

    /// <summary>Writes <paramref name="activity"/>'s context (and current baggage) into <paramref name="carrier"/>.</summary>
    internal static void Inject(Activity activity, IDictionary<string, string> carrier)
    {
        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(activity.Context, Baggage.Current),
            carrier,
            static (c, key, value) => c[key] = value);

        // Authoritative for this activity: overwrites whatever an inherited carrier held.
        carrier[TraceParentKey] = FormatTraceParent(activity.Context);
        if (activity.TraceStateString is { Length: > 0 } traceState)
            carrier[TraceStateKey] = traceState;
        else
            carrier.Remove(TraceStateKey);
    }

    /// <summary>
    /// Reads the trace context from <paramref name="metadata"/>: W3C keys first, then the legacy
    /// <c>_traceId</c>/<c>_spanId</c> pair. Returns <c>default</c> when there is none.
    /// </summary>
    internal static ActivityContext Extract(IReadOnlyDictionary<string, string> metadata)
    {
        var extracted = Propagators.DefaultTextMapPropagator.Extract(
            default,
            metadata,
            static (m, key) => m.TryGetValue(key, out var v) ? [v] : []).ActivityContext;
        if (extracted != default)
            return extracted;

        if (metadata.TryGetValue(TraceParentKey, out var traceParent)
            && ActivityContext.TryParse(
                traceParent,
                metadata.GetValueOrDefault(TraceStateKey),
                isRemote: true,
                out var direct))
            return direct;

        // The legacy keys never recorded the sampled flag; they were always read back as sampled.
        return metadata.TryGetValue(LegacyTraceIdKey, out var traceId)
            && metadata.TryGetValue(LegacySpanIdKey, out var spanId)
            && ActivityContext.TryParse($"00-{traceId}-{spanId}-01", null, isRemote: true, out var legacy)
                ? legacy
                : default;
    }

    /// <summary>W3C <c>traceparent</c> rendering of <paramref name="context"/>.</summary>
    internal static string FormatTraceParent(ActivityContext context) =>
        $"00-{context.TraceId}-{context.SpanId}-{(byte)context.TraceFlags:x2}";

    /// <summary>
    /// Copies the trace context from one metadata dictionary to another without overwriting,
    /// upgrading a legacy pair to <c>traceparent</c> on the way.
    /// </summary>
    internal static void CarryOver(IReadOnlyDictionary<string, string> source, IDictionary<string, string> target)
    {
        if (source.ContainsKey(TraceParentKey))
        {
            foreach (var key in (ReadOnlySpan<string>)[TraceParentKey, TraceStateKey, BaggageKey])
                if (source.TryGetValue(key, out var value))
                    target.TryAdd(key, value);
        }
        else if (Extract(source) is var legacy && legacy != default)
        {
            target.TryAdd(TraceParentKey, FormatTraceParent(legacy));
        }
    }
}
