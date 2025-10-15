using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Extension methods for adding projections to ChannelSubscriptionsBuilder
/// </summary>
public static class ChannelSubscriptionsBuilderExtensions
{
    /// <summary>
    /// Adds a projection subscription that updates read models based on events
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore factory type</typeparam>
    /// <typeparam name="TSubscription">The subscription handler class</typeparam>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projection state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation</typeparam>
    /// <param name="builder">The channel subscriptions builder</param>
    /// <param name="mode">Subscription mode for this projection (Sync, Async, or Hybrid). Default: Sync</param>
    /// <returns>The builder for chaining</returns>
    public static ChannelSubscriptionsBuilder<TEventStore> AddProjection<TEventStore, TSubscription, TKey, TState,
        TProjector>(
        this ChannelSubscriptionsBuilder<TEventStore> builder,
        SubscriptionMode mode = SubscriptionMode.Sync)
        where TEventStore : EventStoreFactory
        where TSubscription : class, IEventHandler
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        var services = builder.Services;
        var moduleKey = builder.ModuleKey;

        // Register projector
        services.AddKeyedSingleton<TProjector>(moduleKey);

        // Register ProjectionHandler
        services.AddKeyedScoped<ProjectionHandler<TKey, TState>>(moduleKey, (sp, _) =>
        {
            var repository = sp.GetRequiredService<IProjectionRepository<TKey, TState>>();
            var projector = sp.GetRequiredKeyedService<TProjector>(moduleKey);
            var logger = sp.GetRequiredService<ILogger<ProjectionHandler<TKey, TState>>>();
            return new ProjectionHandler<TKey, TState>(repository, projector, logger);
        });

        // Register subscription handler
        services.AddKeyedScoped<TSubscription>(moduleKey, (sp, _) =>
        {
            var handler = sp.GetRequiredKeyedService<ProjectionHandler<TKey, TState>>(moduleKey);
            return (TSubscription)Activator.CreateInstance(typeof(TSubscription), handler)!;
        });

        // Register handler with mode tracking
        builder.AddHandler<TSubscription>(mode);

        return builder;
    }
}