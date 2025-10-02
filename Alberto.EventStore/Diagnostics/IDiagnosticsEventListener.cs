using Alberto.EventStore.Events;

namespace Alberto.EventStore.Diagnostics;

public interface IDiagnosticsEventListener
{
    IDisposable Stream(StreamQuery query, int? maxCount);
    IDisposable Append(IEventToPersist[] events);
}