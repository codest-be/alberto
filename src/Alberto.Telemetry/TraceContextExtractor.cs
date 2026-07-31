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
    internal static ActivityLink? ExtractTraceLink(IEventEnvelope envelope, ITraceContextProvider? provider)
    {
        if (provider is null)
            return null;

        var traceContext = provider.ExtractTraceContext(envelope.Metadata);
        if (traceContext is null)
            return null;

        // W3C traceparent format: 00-{traceId}-{spanId}-01
        if (!ActivityContext.TryParse(
            $"00-{traceContext.TraceId}-{traceContext.SpanId}-01",
            null,
            out var activityContext))
            return null;

        return new ActivityLink(activityContext, new ActivityTagsCollection
        {
            { "event.position", envelope.GlobalPosition }
        });
    }
}
