using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;

namespace Alberto.EventStore;

public class EventStoreFactory(
    ITenantContext tenantContext,
    IEventStoreBackend backend,
    IDiagnosticsEventListener? diagnostics)
{
    private readonly IDiagnosticsEventListener _diagnostics = diagnostics ?? new NoopDiagnosticsEventListener();

    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        using IDisposable streamScope = _diagnostics.Stream(query, maxCount);

        return backend.Stream(tenantContext.Tenant, query, maxCount, cancellationToken);
    }

    public Task<IEnumerable<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLatestEventId,
        CancellationToken cancellationToken = default)
    {
        IEventToPersist[] eventToPersists = events as IEventToPersist[] ?? events.ToArray();

        using IDisposable appendScope = _diagnostics.Append(eventToPersists);

        return backend.Append(tenantContext.Tenant, eventToPersists, consistencyBoundary,
            expectedLatestEventId, cancellationToken);
    }
}