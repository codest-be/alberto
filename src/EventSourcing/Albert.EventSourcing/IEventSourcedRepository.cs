using Alberto.EventStore;

namespace Albert.EventSourcing;

/// <summary>
/// Repository for loading and persisting event-sourced aggregates.
/// Hides the complexity of event loading, state projection, and optimistic concurrency.
/// </summary>
/// <typeparam name="TState">The type of the aggregate state</typeparam>
public interface IEventSourcedRepository<TState> where TState : new()
{
    /// <summary>
    /// Loads an aggregate by querying events and projecting them into state.
    /// Returns an Aggregate with current state and lastEventId for concurrency control.
    /// </summary>
    /// <param name="query">The stream query to filter events</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An aggregate containing the current state and metadata</returns>
    Task<Aggregate<TState>> Load(StreamQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves events for an existing aggregate with optimistic concurrency check.
    /// Uses the lastEventId from the aggregate for concurrency control.
    /// </summary>
    /// <param name="query">The consistency boundary query</param>
    /// <param name="aggregate">The aggregate containing current state and lastEventId</param>
    /// <param name="events">The events to persist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task Save(StreamQuery query, Aggregate<TState> aggregate, IEnumerable<object> events,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves events for a new aggregate (no concurrency check).
    /// Convenience method equivalent to Save with expectedLastEventId = null.
    /// </summary>
    /// <param name="query">The consistency boundary query</param>
    /// <param name="events">The events to persist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveNew(StreamQuery query, IEnumerable<object> events, CancellationToken cancellationToken = default);
}