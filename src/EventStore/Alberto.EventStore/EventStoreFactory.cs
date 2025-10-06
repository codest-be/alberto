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

        // Enhance events with telemetry metadata after append activity is created
        var enhancedEvents = EnhanceEventsWithTelemetry(eventToPersists);

        return backend.Append(tenantContext.Tenant, enhancedEvents, consistencyBoundary,
            expectedLatestEventId, cancellationToken);
    }

    private IEventToPersist[] EnhanceEventsWithTelemetry(IEventToPersist[] events)
    {
        // Get telemetry metadata from diagnostics listener
        var telemetryMetadata = _diagnostics.GetTelemetryMetadata();
        if (!telemetryMetadata.Any()) return events;

        foreach (var evt in events)
        {
            foreach (var kvp in telemetryMetadata)
            {
                evt.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return events;
    }
}