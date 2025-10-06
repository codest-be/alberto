using Alberto.EventStore.Events;

namespace Alberto.EventStore.Diagnostics;

public interface IDiagnosticsEventListener
{
    IDisposable Stream(StreamQuery query, int? maxCount);
    IDisposable Append(IEventToPersist[] events);

    /// <summary>
    /// Gets telemetry metadata to be added to events (e.g., trace IDs)
    /// </summary>
    /// <returns>Dictionary containing telemetry metadata, or empty dictionary if no telemetry is available</returns>
    Dictionary<string, string> GetTelemetryMetadata();
}