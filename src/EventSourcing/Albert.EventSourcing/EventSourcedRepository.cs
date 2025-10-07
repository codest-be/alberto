using Albert.EventSourcing.Projectors;
using Alberto.EventStore;

namespace Albert.EventSourcing;

/// <summary>
/// Default implementation of IEventSourcedRepository that uses EventStoreFactory and IProjector.
/// </summary>
/// <typeparam name="TState">The type of the aggregate state</typeparam>
public sealed class EventSourcedRepository<TState>(EventStoreFactory eventStore, IProjector<TState> projector)
    : IEventSourcedRepository<TState>
    where TState : new()
{
    /// <inheritdoc />
    public async Task<Aggregate<TState>> Load(StreamQuery query, CancellationToken cancellationToken = default)
    {
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);

        if (events.Count == 0)
            return Aggregate<TState>.Empty();

        var state = projector.Evolve(events);

        return Aggregate<TState>.Create(state, lastEventId);
    }

    /// <inheritdoc />
    public Task Save(StreamQuery query, Aggregate<TState> aggregate, IEnumerable<object> events,
        CancellationToken cancellationToken = default)
    {
        return eventStore.Persist(query, aggregate.LastEventId, events, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveNew(StreamQuery query, IEnumerable<object> events, CancellationToken cancellationToken = default)
    {
        return eventStore.PersistNew(query, events, cancellationToken);
    }
}