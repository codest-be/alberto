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
    /// <param name="subscriptionName">Name of the subscription processing the event</param>
    /// <param name="eventType">Type of the event being processed</param>
    /// <param name="isSynchronous">True for synchronous subscriptions (same trace), false for asynchronous (linked trace)</param>
    /// <returns>A disposable scope that maintains trace context</returns>
    IDisposable CreateScopeFromMetadata(IReadOnlyDictionary<string, string> metadata, string subscriptionName,
        string eventType, bool isSynchronous);
}

/// <summary>
/// No-op implementation for when telemetry is not enabled
/// </summary>
public class NoopTraceContextProvider : ITraceContextProvider
{
    public IDisposable CreateScopeFromMetadata(IReadOnlyDictionary<string, string> metadata, string subscriptionName,
        string eventType, bool isSynchronous)
    {
        return new NoopDisposable();
    }
}