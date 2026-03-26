#pragma warning disable CS0618 // Obsolete projection types used intentionally for backward-compatibility
namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Event consumer that routes events from the store to processors.
/// Consumers are "dumb" routers - they handle delivery, not logic.
/// </summary>
public interface IEventConsumer : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for this consumer.
    /// </summary>
    string ConsumerId { get; }

    /// <summary>
    /// Register a processor to receive events.
    /// </summary>
    void RegisterProcessor(IEventProcessor processor);

    /// <summary>
    /// Start consuming events and routing them to processors.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop consuming events gracefully.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Extension methods for registering projections and reactors.
/// </summary>
public static class EventConsumerExtensions
{
    /// <summary>
    /// Register a projection to run asynchronously via this consumer.
    /// Uses the same projection definition as inline projections.
    /// </summary>
    /// <typeparam name="TState">The projection state type.</typeparam>
    /// <typeparam name="TProjection">The projection implementation type.</typeparam>
    /// <param name="consumer">The consumer to register with.</param>
    /// <param name="stateStoreFactory">Factory to create a state store.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    public static void RegisterProjection<TState, TProjection>(
        this IEventConsumer consumer,
        Func<IStateStore<TState>> stateStoreFactory,
        string? processorId = null)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
        var processor = new AsyncProjection<TState, TProjection>(
            stateStoreFactory,
            processorId ?? typeof(TProjection).Name);
        consumer.RegisterProcessor(processor);
    }

    /// <summary>
    /// Register a reactor to run asynchronously via this consumer.
    /// The reactor should implement IReact&lt;TEvent&gt; for each event it handles.
    /// No base class inheritance required.
    /// </summary>
    /// <typeparam name="TReactor">The reactor type implementing IReact&lt;TEvent&gt; interfaces.</typeparam>
    /// <param name="consumer">The consumer to register with.</param>
    /// <param name="reactor">The reactor instance.</param>
    /// <param name="processorId">Optional processor ID. Defaults to reactor type name.</param>
    public static void RegisterReactor<TReactor>(
        this IEventConsumer consumer,
        TReactor reactor,
        string? processorId = null)
        where TReactor : class
    {
        var processor = new AsyncReactor<TReactor>(
            reactor,
            processorId ?? typeof(TReactor).Name);
        consumer.RegisterProcessor(processor);
    }
}
