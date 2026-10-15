using System.Diagnostics;
using Alberto.Diagnostics;

namespace Alberto.Telemetry;

/// <summary>
/// Shared helper for extracting an <see cref="ActivityLink"/> from event envelope
/// metadata when an <see cref="ITraceContextProvider"/> is available.
/// Used by both <see cref="TelemetryConsumeMiddleware"/> and
/// <see cref="TelemetryBatchConsumeMiddleware"/> to avoid duplicating W3C
/// traceparent parsing logic across the two middleware implementations.
/// </summary>
internal static class TraceContextExtractor
{
    /// <summary>
    /// Returns an <see cref="ActivityLink"/> to the trace that originally appended
    /// <paramref name="envelope"/>, or <see langword="null"/> when:
    /// <list type="bullet">
    ///   <item><paramref name="provider"/> is <see langword="null"/>.</item>
    ///   <item>The provider finds no trace context in the envelope metadata.</item>
    ///   <item>The stored trace IDs cannot be parsed as a W3C traceparent.</item>
    /// </list>
    /// </summary>
    /// <param name="envelope">The event envelope to inspect.</param>
    /// <param name="provider">
    /// Extracts trace context from the envelope metadata. May be
    /// <see langword="null"/> when no provider is registered.
    /// </param>
    internal static ActivityLink? ExtractTraceLink(IEventEnvelope envelope, ITraceContextProvider? provider) =>
        ExtractContext(envelope, provider) is { } context
            ? new ActivityLink(context, new ActivityTagsCollection { { "event.position", envelope.GlobalPosition } })
            : null;

    /// <summary>
    /// The remote <see cref="ActivityContext"/> of the trace that appended <paramref name="envelope"/>,
    /// or <see langword="null"/> under the same conditions as <see cref="ExtractTraceLink"/>.
    /// </summary>
    internal static ActivityContext? ExtractContext(IEventEnvelope envelope, ITraceContextProvider? provider)
    {
        if (provider is null)
            return null;

        var traceContext = provider.ExtractTraceContext(envelope.Metadata);
        if (traceContext is null)
            return null;

        return ActivityContext.TryParse(
            traceContext.TraceParent,
            traceContext.TraceState,
            isRemote: true,
            out var activityContext)
            ? activityContext
            : null;
    }

    /// <summary>
    /// One link per distinct upstream trace in <paramref name="envelopes"/> (first event of each
    /// trace wins), at most <see cref="MaxBatchLinks"/>. <paramref name="dropped"/> counts the
    /// distinct traces that did not fit.
    /// </summary>
    internal static ActivityLink[] ExtractBatchLinks(
        IReadOnlyList<IEventEnvelope> envelopes, ITraceContextProvider? provider, out int dropped)
    {
        dropped = 0;
        if (provider is null)
            return [];

        var links = new List<ActivityLink>();
        var seen = new HashSet<ActivityTraceId>();
        foreach (var envelope in envelopes)
        {
            if (ExtractContext(envelope, provider) is not { } context || !seen.Add(context.TraceId))
                continue;
            if (links.Count == MaxBatchLinks)
                dropped++;
            else
                links.Add(new ActivityLink(context, new ActivityTagsCollection { { "event.position", envelope.GlobalPosition } }));
        }

        return [.. links];
    }

    /// <summary>Upper bound on links per batch span; backends truncate far above this anyway.</summary>
    internal const int MaxBatchLinks = 32;
}
