using EventStore.Events;
using EventStore.MultiTenant;

namespace EventStore;

/// <summary>
/// Interface for the event store
/// </summary>
public interface IEventStoreBackend
{
    /// <summary>
    /// Queries events matching the specified criteria
    /// </summary>
    /// <param name="tenant">The tenant context for the event store operation</param>
    /// <param name="query">The stream query criteria</param>
    /// <param name="maxCount">Optional maximum number of events to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of matching events</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        Tenant tenant,
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Conditionally appends events to the event store with optimistic concurrency check
    /// </summary>
    /// <param name="tenant">The tenant for whom the events are being appended</param>
    /// <param name="events">The events to append</param>
    /// <param name="consistencyBoundary">The query that defines the consistency boundary</param>
    /// <param name="expectedLastEventId">The expected ID of the last event in the consistency boundary</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating success or failure</returns>
    Task<IEnumerable<IEventEnvelope>> Append(
        Tenant tenant,
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken = default);
}