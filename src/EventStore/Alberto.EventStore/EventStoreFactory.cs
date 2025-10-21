using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Channel;

namespace Alberto.EventStore;

/// <summary>
/// Base class for typed EventStore modules. Inherit from this class to create domain-specific event stores.
/// </summary>
/// <remarks>
/// Example: <c>public class OrderEventStore : EventStoreFactory { }</c>
/// </remarks>
public class EventStoreFactory(
    ITenantContext tenantContext,
    IEventStoreBackend backend,
    ChannelSubscriptionRegistry channelRegistry,
    IDiagnosticsEventListener? diagnostics)
{
    private readonly IDiagnosticsEventListener _diagnostics = diagnostics ?? new NoopDiagnosticsEventListener();

    /// <summary>
    /// Queries events matching the specified criteria.
    /// </summary>
    /// <param name="query">Query criteria for filtering events</param>
    /// <param name="maxCount">Maximum number of events to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of events matching the query</returns>
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        using IDisposable streamScope = _diagnostics.Stream(query, maxCount);

        return backend.Stream(tenantContext.Tenant, query, maxCount, cancellationToken);
    }

    /// <summary>
    /// Appends events to the event store with optional optimistic concurrency check.
    /// </summary>
    /// <param name="events">Events to append</param>
    /// <param name="consistencyBoundary">Query defining the consistency boundary for concurrency check</param>
    /// <param name="expectedLatestEventId">Expected ID of the last event in the consistency boundary</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Appended events with populated metadata</returns>
    /// <exception cref="ConcurrencyConflictException">Thrown when optimistic concurrency check fails</exception>
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

        IEnumerable<IEventEnvelope> eventEnvelopes = result as IEventEnvelope[] ?? result.ToArray();
        var eventArray = eventEnvelopes.ToArray();

        var globalEvents = ConvertToGlobalEventEnvelopes(eventArray, tenantContext.Tenant.Id);

#pragma warning disable CS4014
        if (globalEvents.Count > 0) channelRegistry.NotifySubscriptions(globalEvents, cancellationToken);
#pragma warning restore CS4014

        return eventArray;
    }

    private IEventToPersist[] EnhanceEventsWithTelemetry(IEventToPersist[] events)
    {
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

    private static List<GlobalEventEnvelope> ConvertToGlobalEventEnvelopes(
        IEnumerable<IEventEnvelope> events,
        string tenantId)
    {
        var globalEvents = new List<GlobalEventEnvelope>();

        foreach (var evt in events)
        {
            var globalEvent = new GlobalEventEnvelope(
                evt.Position,
                evt.Id,
                tenantId,
                evt.EventType.Id,
                evt.EventJson,
                new Dictionary<string, string>(evt.Metadata),
                evt.Created
            );

            globalEvents.Add(globalEvent);
        }

        return globalEvents;
    }
}