using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Channel;

namespace Alberto.EventStore;

public class EventStoreFactory(
    ITenantContext tenantContext,
    IEventStoreBackend backend,
    ChannelSubscriptionRegistry channelRegistry,
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

    public async Task<IEnumerable<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLatestEventId,
        CancellationToken cancellationToken = default)
    {
        IEventToPersist[] eventToPersists = events as IEventToPersist[] ?? events.ToArray();

        using IDisposable appendScope = _diagnostics.Append(eventToPersists);

        // Enhance events with telemetry metadata after append activity is created
        var enhancedEvents = EnhanceEventsWithTelemetry(eventToPersists);

        var result = await backend.Append(tenantContext.Tenant, enhancedEvents, consistencyBoundary,
            expectedLatestEventId, cancellationToken);

        // Notify channel subscriptions after successful append
        IEnumerable<IEventEnvelope> eventEnvelopes = result as IEventEnvelope[] ?? result.ToArray();
        var eventArray = eventEnvelopes.ToArray();

        var globalEvents = ConvertToGlobalEventEnvelopes(eventArray, tenantContext.Tenant.Id);

        if (globalEvents.Count > 0) await channelRegistry.NotifySubscriptions(globalEvents, cancellationToken);

        return eventArray;
    }

    private IEventToPersist[] EnhanceEventsWithTelemetry(IEventToPersist[] events)
    {
        // Get telemetry metadata from diagnostics listener
        var telemetryMetadata = _diagnostics.GetTelemetryMetadata();
        if (telemetryMetadata.Count == 0) return events;

        foreach (var evt in events)
        {
            foreach (var kvp in telemetryMetadata)
            {
                evt.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return events;
    }

    private List<GlobalEventEnvelope> ConvertToGlobalEventEnvelopes(
        IEnumerable<IEventEnvelope> events,
        string tenantId)
    {
        var globalEvents = new List<GlobalEventEnvelope>();

        foreach (var evt in events)
        {
            // Extract tags from the original event data
            // Note: We don't have direct access to tags here, so we'll need to handle this differently
            // For now, pass empty array - subscribers will need to deserialize if they need tags
            var globalEvent = new GlobalEventEnvelope(
                evt.Position,
                evt.Id,
                tenantId,
                evt.EventType.Id,
                Array.Empty<string>(), // Tags not available at this level
                evt.EventJson,
                new Dictionary<string, string>(evt.Metadata),
                evt.Created
            );

            globalEvents.Add(globalEvent);
        }

        return globalEvents;
    }
}