using System.Diagnostics;
using EventStore.Diagnostics;
using EventStore.Events;

namespace EventStore;

public sealed class EventStore(IEventStoreBackend backend, IDiagnosticsEventListener diagnostics)
{
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        using var streamScope = diagnostics.Stream(query, maxCount);
        
        return backend.Stream(query, maxCount, cancellationToken);
    }

    public Task<IEnumerable<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLatestEventId,
        CancellationToken cancellationToken = default)
    {
        var eventToPersists = events as IEventToPersist[] ?? events.ToArray();

        using var appendScope = diagnostics.Append(eventToPersists);
        
        return backend.Append(eventToPersists, consistencyBoundary, expectedLatestEventId, cancellationToken);
    }
}