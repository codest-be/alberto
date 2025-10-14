using Alberto.EventSourcing.Projectors;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Projections.InMemory;

/// <summary>
/// Extension methods for registering in-memory projection repositories in dependency injection.
/// </summary>
public static class InMemoryProjectionRepositoryExtensions
{
    /// <summary>
    /// Registers a projection repository with in-memory backend for a specific state type.
    /// The type parameters (TKey, TState) provide natural isolation - no keying needed.
    /// Now tenant-aware: each tenant's projections are isolated using composite keys.
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
        // Register projector
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        // Register in-memory repository backend as scoped (to support tenant context)
        // Type signature provides isolation (e.g., IProjectionRepository<Guid, OrderState> vs IProjectionRepository<Guid, PaymentState>)
        services.AddSingleton<IProjectionRepository<TKey, TState>, InMemoryProjectionRepository<TKey, TState>>();

        // Register projection handler helper
        services.AddScoped<ProjectionHandler<TKey, TState>>();

        return services;
    }
}