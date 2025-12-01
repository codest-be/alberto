using Alberto.EventSourcing.Aggregates;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;

namespace Alberto.EventSourcing;

/// <summary>
/// Extension methods for executing decisions on aggregates.
/// Provides fluent API to reduce boilerplate in command handlers.
/// </summary>
public static class AggregateExtensions
{
    /// <summary>
    /// Executes a decision on an existing aggregate: Load → Decide → Persist.
    /// Recommended for most command handlers that operate on existing aggregates.
    /// </summary>
    /// <typeparam name="TState">The type of the aggregate's state</typeparam>
    /// <param name="eventStore"></param>
    /// <param name="projector">The projector that projects events into state</param>
    /// <param name="query">The stream query specifying which events to load</param>
    /// <param name="decide">Function that takes the current state and returns a Decision</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The decision result after persistence</returns>
    public static async Task<Decision> Decide<TState>(
        this EventStoreFactory eventStore,
        IProjector<TState> projector,
        StreamQuery query,
        Func<TState, Decision> decide,
        CancellationToken cancellationToken = default) where TState : new()
    {
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = projector.Evolve(events);
        var decision = decide(state);

        if (decision.IsError) return decision;

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);
        return Decision.Succeed();
    }

    /// <summary>
    /// Executes a decision on an existing aggregate with return value: Load → Decide → Persist.
    /// Use for commands that return values while operating on existing aggregates.
    /// </summary>
    /// <typeparam name="TState">The type of the aggregate's state</typeparam>
    /// <typeparam name="TValue">The type of value returned by the decision</typeparam>
    /// <param name="eventStore"></param>
    /// <param name="projector">The projector that projects events into state</param>
    /// <param name="query">The stream query specifying which events to load</param>
    /// <param name="decide">Function that takes the current state and returns a Decision&lt;TValue&gt;</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The decision result after persistence</returns>
    public static async Task<Decision<TValue>> Decide<TState, TValue>(
        this EventStoreFactory eventStore,
        IProjector<TState> projector,
        StreamQuery query,
        Func<TState, Decision<TValue>> decide,
        CancellationToken cancellationToken = default) where TState : new()
    {
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = projector.Evolve(events);
        var decision = decide(state);

        if (decision.IsError) return decision;

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);
        return Decision<TValue>.Succeed(decision.Value);
    }

    /// <summary>
    /// Executes a decision on a new aggregate: Decide → Persist.
    /// Use for commands that create new aggregates.
    /// </summary>
    /// <param name="eventStore"></param>
    /// <param name="query">The stream query specifying where to persist events</param>
    /// <param name="decide">Function that returns a Decision</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The decision result after persistence</returns>
    public static async Task<Decision> DecideNew(this EventStoreFactory eventStore,
        StreamQuery query,
        Func<Decision> decide,
        CancellationToken cancellationToken = default)
    {
        var decision = decide();
        if (decision.IsError) return decision;

        await eventStore.Persist(query, null, decision.Events, cancellationToken);
        return Decision.Succeed();
    }

    /// <summary>
    /// Executes a decision on a new aggregate with return value: Decide → Persist.
    /// Use for commands that create new aggregates and return values (e.g., CreateOrder returning Guid).
    /// </summary>
    /// <typeparam name="TValue">The type of value returned by the decision</typeparam>
    /// <param name="eventStore"></param>
    /// <param name="query">The stream query specifying where to persist events</param>
    /// <param name="decide">Function that returns a Decision&lt;TValue&gt;</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The decision result after persistence</returns>
    public static async Task<Decision<TValue>> DecideNew<TValue>(this EventStoreFactory eventStore,
        StreamQuery query,
        Func<Decision<TValue>> decide,
        CancellationToken cancellationToken = default)
    {
        var decision = decide();
        if (decision.IsError) return decision;

        await eventStore.Persist(query, null, decision.Events, cancellationToken);
        return Decision<TValue>.Succeed(decision.Value);
    }

    /// <summary>
    /// Executes a decision on an existing aggregate using a shared aggregate projector.
    /// Use for "real aggregates" where multiple commands operate on the same state.
    /// </summary>
    /// <typeparam name="TState">The aggregate's state type</typeparam>
    /// <param name="eventStore"></param>
    /// <param name="aggregate">The aggregate projector (shared across commands)</param>
    /// <param name="aggregateId">The aggregate identifier</param>
    /// <param name="decide">Function that takes the current state and returns a Decision</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The decision result after persistence</returns>
    public static async Task<Decision> Decide<TState>(
        this EventStoreFactory eventStore,
        IAggregateProjector<TState> aggregate,
        string aggregateId,
        Func<TState, Decision> decide,
        CancellationToken cancellationToken = default) where TState : new()
    {
        var query = aggregate.GetQuery(aggregateId);
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = aggregate.Evolve(events);
        var decision = decide(state);

        if (decision.IsError) return decision;

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);
        return Decision.Succeed();
    }

    /// <summary>
    /// Executes a decision on an existing aggregate with return value using a shared aggregate projector.
    /// Use for "real aggregates" where multiple commands operate on the same state.
    /// </summary>
    /// <typeparam name="TState">The aggregate's state type</typeparam>
    /// <typeparam name="TValue">The type of value returned by the decision</typeparam>
    /// <param name="eventStore"></param>
    /// <param name="aggregate">The aggregate projector (shared across commands)</param>
    /// <param name="aggregateId">The aggregate identifier</param>
    /// <param name="decide">Function that takes the current state and returns a Decision&lt;TValue&gt;</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The decision result after persistence</returns>
    public static async Task<Decision<TValue>> Decide<TState, TValue>(
        this EventStoreFactory eventStore,
        IAggregateProjector<TState> aggregate,
        string aggregateId,
        Func<TState, Decision<TValue>> decide,
        CancellationToken cancellationToken = default) where TState : new()
    {
        var query = aggregate.GetQuery(aggregateId);
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = aggregate.Evolve(events);
        var decision = decide(state);

        if (decision.IsError) return decision;

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);
        return Decision<TValue>.Succeed(decision.Value);
    }

    /// <summary>
    /// Executes a decision on a new aggregate using a shared aggregate projector.
    /// Use for creating new aggregates with the aggregate pattern.
    /// </summary>
    /// <typeparam name="TState">The aggregate's state type</typeparam>
    /// <typeparam name="TValue">The type of value returned by the decision</typeparam>
    /// <param name="eventStore"></param>
    /// <param name="aggregate">The aggregate projector (shared across commands)</param>
    /// <param name="aggregateId">The aggregate identifier for the new aggregate</param>
    /// <param name="decide">Function that returns a Decision&lt;TValue&gt;</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The decision result after persistence</returns>
    public static async Task<Decision<TValue>> DecideNew<TState, TValue>(
        this EventStoreFactory eventStore,
        IAggregateProjector<TState> aggregate,
        string aggregateId,
        Func<Decision<TValue>> decide,
        CancellationToken cancellationToken = default) where TState : new()
    {
        var query = aggregate.GetQuery(aggregateId);
        var decision = decide();
        if (decision.IsError) return decision;

        await eventStore.Persist(query, null, decision.Events, cancellationToken);
        return Decision<TValue>.Succeed(decision.Value);
    }
}