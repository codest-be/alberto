using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;

namespace Alberto.EventStore;


public abstract class EventStore(EventStoreFactory factory)
{
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default) => factory.Stream(query, maxCount, cancellationToken);

    public Task<IEnumerable<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLatestEventId,
        CancellationToken cancellationToken = default) =>
        factory.Append(events, consistencyBoundary, expectedLatestEventId, cancellationToken);
}

public class EventStoreFactory(
    ITenantContext tenantContext,
    IDiagnosticsEventListener diagnostics,
    IEventStoreBackend backend)
{
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        using var streamScope = diagnostics.Stream(query, maxCount);

        return backend.Stream(tenantContext.Tenant, query, maxCount, cancellationToken);
    }

    public Task<IEnumerable<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLatestEventId,
        CancellationToken cancellationToken = default)
    {
        var eventToPersists = events as IEventToPersist[] ?? events.ToArray();

        using var appendScope = diagnostics.Append(eventToPersists);

        return backend.Append(tenantContext.Tenant, eventToPersists, consistencyBoundary,
            expectedLatestEventId, cancellationToken);
    }
}