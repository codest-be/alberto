namespace Alberto.Diagnostics;

/// <summary>
/// Extracts trace context from event metadata for distributed trace linking.
/// </summary>
public interface ITraceContextProvider
{
    /// <summary>
    /// Extracts trace context from event metadata if present.
    /// </summary>
    /// <param name="metadata">The event metadata dictionary.</param>
    /// <returns>Trace context if found in metadata, null otherwise.</returns>
    TraceContext? ExtractTraceContext(IReadOnlyDictionary<string, string> metadata);
}

/// <summary>
/// Represents W3C trace context extracted from event metadata.
/// </summary>
/// <param name="TraceId">The trace ID (32 hex characters).</param>
/// <param name="SpanId">The span ID (16 hex characters).</param>
public sealed record TraceContext(string TraceId, string SpanId);
