using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.Projections.InMemory;

/// <summary>
/// Extension methods for adding in-memory projections to channel subscriptions
/// </summary>
public static class InMemoryProjectionExtensions
{
    /// <summary>
    /// Adds a projection subscription with in-memory repository backend.
    /// Combines repository registration and subscription setup in a single call.
    /// Ideal for testing and development scenarios.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore factory type</typeparam>
    /// <typeparam name="TSubscription">The subscription handler class</typeparam>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projection state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation</typeparam>
    /// <param name="builder">The channel subscriptions builder</param>
    /// <param name="mode">Subscription mode for this projection (Sync, Async, or Hybrid). Default: Sync</param>
    /// <returns>The builder for chaining</returns>
    public static ChannelSubscriptionsBuilder<TEventStore> AddInMemoryProjection<TEventStore, TSubscription, TKey,
        TState, TProjector>(
        this ChannelSubscriptionsBuilder<TEventStore> builder,
        SubscriptionMode mode = SubscriptionMode.Sync)
        where TEventStore : EventStoreFactory
        where TSubscription : class, IProjectionSubscription, IEventHandler
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        // Register in-memory repository
        builder.Services.AddInMemoryProjectionRepository<TKey, TState, TProjector>();

        // Register projection subscription
        return builder.AddProjection<TSubscription, TKey, TState>(mode);
    }
}