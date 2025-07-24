using EventStore.Events;

namespace EventStore.Diagnostics;

public interface IDiagnosticsEventListener
{
    IDisposable Stream(StreamQuery query, int? maxCount);
    IDisposable Append(IEventToPersist[] events);
}