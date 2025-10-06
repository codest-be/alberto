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

        // Enhance events with telemetry metadata through diagnostics abstraction
        var enhancedEvents = EnhanceEventsWithTelemetry(eventToPersists);

        using IDisposable appendScope = _diagnostics.Append(enhancedEvents);

        return backend.Append(tenantContext.Tenant, enhancedEvents, consistencyBoundary,
            expectedLatestEventId, cancellationToken);
    }

    private IEventToPersist[] EnhanceEventsWithTelemetry(IEventToPersist[] events)
    {
        return events.Select(evt =>
        {
            // Get telemetry metadata from diagnostics listener
            var telemetryMetadata = _diagnostics.GetTelemetryMetadata();

            // Merge user metadata with telemetry metadata (telemetry takes precedence)
            var enhancedMetadata = new Dictionary<string, string>(evt.Metadata);
            foreach (var kvp in telemetryMetadata)
            {
                enhancedMetadata[kvp.Key] = kvp.Value;
            }

            // Return a new EventToPersist with enhanced metadata
            return new EventToPersist
            {
                Tags = evt.Tags,
                EventJson = evt.EventJson,
                EventType = evt.EventType,
                Metadata = enhancedMetadata,
                Created = evt.Created
            };
        }).ToArray();
    }
}