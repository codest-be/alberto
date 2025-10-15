using Alberto.EventSourcing.Projections;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.Projections.Postgres;

/// <summary>
/// Extension methods for adding Postgres-backed projections to channel subscriptions
/// </summary>
public static class PostgresProjectionExtensions
{
    /// <summary>
    /// Adds a projection subscription with Postgres repository backend.
    /// Combines repository registration and subscription setup in a single call.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore factory type</typeparam>
    /// <typeparam name="TSubscription">The subscription handler class</typeparam>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projection state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation</typeparam>
    /// <param name="builder">The channel subscriptions builder</param>
    /// <param name="mode">Subscription mode for this projection (Sync, Async, or Hybrid). Default: Sync</param>
    /// <returns>The builder for chaining</returns>
    public static ChannelSubscriptionsBuilder<TEventStore> AddPostgresProjection<TEventStore, TSubscription, TKey,
        TState, TProjector>(
        this ChannelSubscriptionsBuilder<TEventStore> builder,
        SubscriptionMode mode = SubscriptionMode.Sync)
        where TEventStore : EventStoreFactory
        where TSubscription : class, IEventHandler
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        // Register Postgres repository (inherits connection/schema from EventStore module)
        builder.Services.AddPostgresProjectionRepository<TKey, TState, TProjector>(builder.ModuleKey);

        // Register projection subscription
        return builder.AddProjection<TEventStore, TSubscription, TKey, TState, TProjector>(mode);
    }
}