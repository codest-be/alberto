namespace Alberto.EventStore.Diagnostics;

/// <summary>
/// Provides telemetry trace context operations for subscriptions
/// </summary>
public interface ITraceContextProvider
{
    /// <summary>
    /// Creates a telemetry scope from event metadata (e.g., for tracing event consumption)
    /// </summary>
    /// <param name="metadata">Event metadata that may contain trace information</param>
    /// <returns>A disposable scope that maintains trace context</returns>
    IDisposable CreateScopeFromMetadata(IReadOnlyDictionary<string, string> metadata);
}

/// <summary>
/// No-op implementation for when telemetry is not enabled
/// </summary>
public class NoopTraceContextProvider : ITraceContextProvider
{
    public IDisposable CreateScopeFromMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        return new NoopDisposable();
    }
}