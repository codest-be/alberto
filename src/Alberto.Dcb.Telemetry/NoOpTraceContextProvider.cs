using Alberto.Dcb.Diagnostics;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// No-op trace context provider used when telemetry is disabled.
/// Always returns <see langword="null"/> — no trace context is extracted from metadata.
/// </summary>
internal sealed class NoOpTraceContextProvider : ITraceContextProvider
{
    internal static readonly NoOpTraceContextProvider Instance = new();

    private NoOpTraceContextProvider() { }

    /// <inheritdoc />
    public TraceContext? ExtractTraceContext(IReadOnlyDictionary<string, string> metadata) => null;
}
