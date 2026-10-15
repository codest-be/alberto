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
/// <param name="TraceParent">The W3C <c>traceparent</c> value, including the sampled flag.</param>
/// <param name="TraceState">The W3C <c>tracestate</c> value, or <see langword="null"/> when there is none.</param>
public sealed record TraceContext(string TraceParent, string? TraceState = null);
