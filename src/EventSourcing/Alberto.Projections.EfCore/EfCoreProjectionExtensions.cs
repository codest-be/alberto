using System.Reflection;
using Alberto.EventSourcing.Projections;
using Alberto.EventStore.Subscriptions.Batching;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Projections.EfCore;

/// <summary>
/// Extension methods for registering EF Core projection repositories.
/// </summary>
public static class EfCoreProjectionExtensions
{
    /// <summary>
    /// Registers an EF Core-backed projection repository for a specific projection type.
    /// The projection entity must implement IHasKey and IVersionedProjection.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core DbContext type containing the projection entity</typeparam>
    /// <typeparam name="TKey">The type of the projection's primary key</typeparam>
    /// <typeparam name="TState">The projection entity type</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    /// <code>
    /// services.AddDbContext&lt;OrderDbContext&gt;(options => options.UseNpgsql(connectionString));
    /// services.AddEfCoreProjectionRepository&lt;OrderDbContext, Guid, OrderSummary&gt;();
    ///
    /// // In tests, swap to in-memory:
    /// services.AddSingleton&lt;IProjectionRepository&lt;Guid, OrderSummary&gt;, InMemoryProjectionRepository&lt;Guid, OrderSummary&gt;&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddEfCoreProjectionRepository<TDbContext, TKey, TState>(
        this IServiceCollection services)
        where TDbContext : DbContext
        where TState : class, IHasKey<TKey>, IVersionedProjection, new()
        where TKey : notnull
    {
        services.AddScoped<IProjectionRepository<TKey, TState>>(sp =>
        {
            var context = sp.GetRequiredService<TDbContext>();
            return new EfCoreProjectionRepository<TKey, TState>(context);
        });

        return services;
    }

    /// <summary>
    /// Helper extension to check if a projection has already processed an event (idempotency check).
    /// </summary>
    /// <typeparam name="T">The projection type implementing IVersionedProjection</typeparam>
    /// <param name="projection">The projection instance</param>
    /// <param name="globalPosition">The global position of the current event</param>
    /// <returns>True if the event has already been processed, false otherwise</returns>
    public static bool IsAlreadyProcessed<T>(this T projection, long globalPosition)
        where T : IVersionedProjection
    {
        return projection.GlobalVersion >= globalPosition;
    }

    /// <summary>
    /// Adds a projection subscription with EF Core repository backend.
    /// Combines repository registration and subscription setup in a single call.
    /// TKey and TState are automatically inferred from the IProjectionSubscription&lt;TKey, TState&gt; interface.
    /// </summary>
    /// <typeparam name="TSubscription">The subscription handler class implementing IProjectionSubscription&lt;TKey, TState&gt;</typeparam>
    /// <typeparam name="TDbContext">The EF Core DbContext type</typeparam>
    /// <typeparam name="TProjector">The projector implementation</typeparam>
    /// <param name="builder">The channel subscriptions builder</param>
    /// <param name="mode">Subscription mode for this projection (Sync, Async, or Hybrid). Default: Sync</param>
    /// <param name="configureBatching">Optional configuration for batching behavior</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// services.AddModule&lt;OrderEventStore&gt;("orders", module => module
    ///     .WithPostgres(...)
    ///     .WithChannelSubscriptions(channel => channel
    ///         .AddEfCoreProjection&lt;OrderProjectionSubscription, OrderDbContext, OrderProjector&gt;(
    ///             mode: SubscriptionMode.Hybrid)));
    /// </code>
    /// </example>
    public static IChannelSubscriptionsBuilder AddEfCoreProjection<TSubscription, TDbContext, TProjector>(
        this IChannelSubscriptionsBuilder builder,
        SubscriptionMode mode = SubscriptionMode.Sync,
        Action<ProjectionBatchingOptions>? configureBatching = null)
        where TSubscription : class, IProjectionSubscription, IEventHandler
        where TDbContext : DbContext
        where TProjector : class
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

        // Register EF Core repository using reflection
        var repoMethod = typeof(EfCoreProjectionExtensions)
            .GetMethod(nameof(AddEfCoreProjectionRepository), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof(TDbContext), keyType, stateType);

        repoMethod.Invoke(null, new object[] { builder.Services });

        // Register projector
        builder.Services.AddScoped(typeof(TProjector));

        // Register projection subscription using reflection
        var addProjectionMethod = typeof(IChannelSubscriptionsBuilder)
            .GetMethod(nameof(IChannelSubscriptionsBuilder.AddProjection))!
            .MakeGenericMethod(typeof(TSubscription), keyType, stateType);

        return (IChannelSubscriptionsBuilder)addProjectionMethod.Invoke(
            builder,
            [mode, configureBatching])!;
    }
}