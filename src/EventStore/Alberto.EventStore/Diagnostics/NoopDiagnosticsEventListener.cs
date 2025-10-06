using Alberto.EventStore.Events;

namespace Alberto.EventStore.Diagnostics;

public class NoopDiagnosticsEventListener : IDiagnosticsEventListener
{
    public IDisposable Stream(StreamQuery query, int? maxCount)
    {
        return new NoopDisposable();
    }

    public IDisposable Append(IEventToPersist[] events)
    {
        return new NoopDisposable();
    }

    public Dictionary<string, string> GetTelemetryMetadata()
    {
        return new Dictionary<string, string>();
    }
}

public class NoopDisposable : IDisposable
{
    public void Dispose() { }
}