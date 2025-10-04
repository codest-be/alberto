namespace Alberto.EventStore;

/// <summary>
/// Event store interface for cross-tenant operations (subscriptions)
/// </summary>
public interface IMultiTenantEventStore
{
    /// <summary>
    /// Streams all events across all tenants in global position order
    /// </summary>
    /// <param name="fromPosition">The position to start streaming from (exclusive)</param>
    /// <param name="maxCount">Maximum number of events to return</param>
    /// <param name="eventTypes">Optional filter for specific event types. If null or empty, all event types are included</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of events ordered by global position</returns>
    Task<IReadOnlyCollection<GlobalEventEnvelope>> StreamAll(
        long fromPosition,
        int maxCount,
        IReadOnlySet<string>? eventTypes = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Event envelope with tenant information for cross-tenant queries
/// </summary>
/// <param name="GlobalPosition">Global position of this event across all tenants</param>
/// <param name="Id">Unique identifier for this event</param>
/// <param name="TenantId">The tenant ID this event belongs to</param>
/// <param name="EventType">The type of event</param>
/// <param name="Tags">Tags associated with this event</param>
/// <param name="EventJson">The JSON representation of the event data</param>
/// <param name="Metadata">Metadata associated with the event</param>
/// <param name="Created">When this event was created</param>
public sealed record GlobalEventEnvelope(
    long GlobalPosition,
    Guid Id,
    string TenantId,
    string EventType,
    string[] Tags,
    string EventJson,
    Dictionary<string, string> Metadata,
    DateTimeOffset Created
);