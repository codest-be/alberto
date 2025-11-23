using Alberto.EventSourcing.Projections;
using Alberto.EventStore;
using Alberto.EventStore.Subscriptions.Batching;
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
    /// TKey and TState are automatically inferred from the IProjectionSubscription&lt;TKey, TState&gt; interface.
    /// </summary>
    /// <typeparam name="TSubscription">The subscription handler class implementing IProjectionSubscription&lt;TKey, TState&gt;</typeparam>
    /// <typeparam name="TProjector">The projector implementation</typeparam>
    /// <typeparam name="TEventStore">The EventStore factory type (inferred from builder)</typeparam>
    /// <param name="builder">The channel subscriptions builder</param>
    /// <param name="mode">Subscription mode for this projection (Sync, Async, or Hybrid). Default: Sync</param>
    /// <param name="configureBatching">Optional configuration for batching behavior</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// services.AddModule&lt;OrderEventStore&gt;("orders", module => module
    ///     .WithInMemory()
    ///     .WithChannelSubscriptions(channel => channel
    ///         .AddInMemoryProjection&lt;OrderProjectionSubscription, OrderProjector&gt;(
    ///             mode: SubscriptionMode.Sync)));
    /// </code>
    /// </example>
    public static ChannelSubscriptionsBuilder<TEventStore> AddInMemoryProjection<TSubscription, TProjector, TEventStore>(
        this ChannelSubscriptionsBuilder<TEventStore> builder,
        SubscriptionMode mode = SubscriptionMode.Sync,
        Action<ProjectionBatchingOptions>? configureBatching = null)
        where TSubscription : class, IProjectionSubscription, IEventHandler
        where TProjector : class
        where TEventStore : EventStoreFactory
    {
        // Find IProjectionSubscription<TKey, TState> interface on TSubscription
        var projectionInterface = typeof(TSubscription)
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                                 i.GetGenericTypeDefinition() == typeof(IProjectionSubscription<,>));

        if (projectionInterface == null)
        {
            throw new InvalidOperationException(
                $"Type {typeof(TSubscription).Name} must implement IProjectionSubscription<TKey, TState>");
        }

        // Extract TKey and TState from the interface
        var genericArgs = projectionInterface.GetGenericArguments();
        var keyType = genericArgs[0];
        var stateType = genericArgs[1];

        // Register in-memory repository using reflection
        var repoMethod = typeof(InMemoryProjectionRepositoryExtensions)
            .GetMethod(nameof(InMemoryProjectionRepositoryExtensions.AddInMemoryProjectionRepository))!
            .MakeGenericMethod(keyType, stateType, typeof(TProjector));

        repoMethod.Invoke(null, new object[] { builder.Services });

        // Register projection subscription using reflection
        var addProjectionMethod = typeof(ChannelSubscriptionsBuilder<TEventStore>)
            .GetMethod(nameof(ChannelSubscriptionsBuilder<TEventStore>.AddProjection))!
            .MakeGenericMethod(typeof(TSubscription), keyType, stateType);

        return (ChannelSubscriptionsBuilder<TEventStore>)addProjectionMethod.Invoke(
            builder,
            new object?[] { mode, configureBatching })!;
    }
}