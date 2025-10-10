using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Projections.Postgres;

public static class PostgresProjectionRepositoryExtensions
{
    /// <summary>
    /// Registers a Postgres projection repository with per-projection configuration.
    /// Each projection can have its own connection string and settings.
    /// </summary>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projected state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action for this projection's PostgresProjectionOptions</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPostgresProjectionRepository<TKey, TState, TProjector>(
        this IServiceCollection services,
        Action<PostgresProjectionOptions> configure)
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        // Use named options with the state type name as the key
        var optionsName = typeof(TState).FullName ?? typeof(TState).Name;
        services.Configure(optionsName, configure);

        // Register projector
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        // Register repository with named options
        services.AddScoped<IProjectionRepository<TKey, TState>>(sp =>
        {
            var namedOptions = sp.GetRequiredService<IOptionsSnapshot<PostgresProjectionOptions>>();
            var options = Options.Create(namedOptions.Get(optionsName));
            var logger = sp.GetRequiredService<ILogger<PostgresProjectionRepository<TKey, TState>>>();
            var tenantContext = sp.GetRequiredService<ITenantContext>();

            return new PostgresProjectionRepository<TKey, TState>(options, logger, tenantContext);
        });

        // Register projection handler helper
        services.AddScoped<ProjectionHandler<TKey, TState>>();

        return services;
    }

    /// <summary>
    /// Registers a custom projection repository backend with per-projection configuration.
    /// Use this when you have a custom backend implementation.
    /// </summary>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projected state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <typeparam name="TRepository">The custom repository implementation</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="repositoryFactory">Factory function to create the repository instance</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddCustomProjectionRepository<TKey, TState, TProjector, TRepository>(
        this IServiceCollection services,
        Func<IServiceProvider, TRepository> repositoryFactory)
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
        where TRepository : class, IProjectionRepository<TKey, TState>
    {
        // Register projector
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        // Register custom repository using provided factory
        services.AddScoped<IProjectionRepository<TKey, TState>, TRepository>(repositoryFactory);

        // Register projection handler helper
        services.AddScoped<ProjectionHandler<TKey, TState>>();

        return services;
    }
}