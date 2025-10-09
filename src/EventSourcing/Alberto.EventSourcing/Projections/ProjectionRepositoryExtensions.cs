using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Extension methods for registering projection repositories in dependency injection.
/// </summary>
public static class ProjectionRepositoryExtensions
{
    /// <summary>
    /// Registers a projection repository with in-memory backend for a specific state type.
    /// This is a simplified version that uses InMemoryProjectionRepository as the backend.
    /// </summary>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projected state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddInMemoryProjectionRepository<TKey, TState, TProjector>(
        this IServiceCollection services)
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        // Register projector if not already registered
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        // Register in-memory repository backend
        services.AddSingleton<IProjectionRepository<TKey, TState>, InMemoryProjectionRepository<TKey, TState>>();

        // Register projection handler helper
        services.AddScoped<ProjectionHandler<TKey, TState>>();

        return services;
    }

    /// <summary>
    /// Registers a projection repository with in-memory backend for a specific state type within a module.
    /// Used when working with multiple event store modules (e.g., OrderEventStore, PaymentEventStore).
    /// </summary>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projected state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <typeparam name="TEventStore">The typed EventStoreFactory (e.g., OrderEventStore)</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddInMemoryProjectionRepository<TKey, TState, TProjector, TEventStore>(
        this IServiceCollection services)
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
        where TEventStore : EventStoreFactory
    {
        // Register projector scoped to this module
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        // Register in-memory repository backend as singleton (shared across requests)
        // Note: For module-specific repositories, consider using keyed services
        services.AddSingleton<IProjectionRepository<TKey, TState>, InMemoryProjectionRepository<TKey, TState>>();

        // Register projection handler helper
        services.AddScoped<ProjectionHandler<TKey, TState>>();

        return services;
    }

    /// <summary>
    /// Registers a custom projection repository backend.
    /// Use this when you have a custom backend implementation (e.g., PostgresProjectionRepository).
    /// </summary>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projected state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <typeparam name="TRepository">The custom repository implementation</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddProjectionRepository<TKey, TState, TProjector, TRepository>(
        this IServiceCollection services)
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
        where TRepository : class, IProjectionRepository<TKey, TState>
    {
        // Register projector
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        // Register custom repository backend
        services.AddScoped<IProjectionRepository<TKey, TState>, TRepository>();

        // Register projection handler helper
        services.AddScoped<ProjectionHandler<TKey, TState>>();

        return services;
    }
}