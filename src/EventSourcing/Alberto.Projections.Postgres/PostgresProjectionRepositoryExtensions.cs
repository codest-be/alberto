using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Subscriptions.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Projections.Postgres;

public static class PostgresProjectionRepositoryExtensions
{
    /// <summary>
    /// Registers a Postgres projection repository that inherits connection and schema from the EventStore module.
    /// The repository will use the same PostgreSQL connection and schema as its associated EventStore.
    /// </summary>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projected state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="moduleKey">The module key identifying the EventStore instance</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPostgresProjectionRepository<TKey, TState, TProjector>(
        this IServiceCollection services,
        string moduleKey = "default")
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        // Register projector
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        // Register repository - resolve options from EventStore configuration at runtime
        services.AddScoped<IProjectionRepository<TKey, TState>>(sp =>
        {
            // Get EventStore options using IOptionsMonitor (singleton, safe to use here)
            var eventStoreOptionsMonitor = sp.GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>();
            var eventStoreOptions = eventStoreOptionsMonitor.Get(moduleKey);

            // Create projection options that inherit from EventStore
            var projectionOptions = new PostgresProjectionOptions { ConnectionString = eventStoreOptions.ConnectionString, Schema = eventStoreOptions.Schema };

            var logger = sp.GetRequiredService<ILogger<PostgresProjectionRepository<TKey, TState>>>();
            var tenantContext = sp.GetRequiredService<ITenantContext>();

            // Pass projector type to ensure consistent table naming with migrations
            return new PostgresProjectionRepository<TKey, TState>(
                Options.Create(projectionOptions),
                logger,
                tenantContext,
                typeof(TProjector));
        });

        // Register projection handler helper
        services.AddScoped<ProjectionHandler<TKey, TState>>();

        return services;
    }

    /// <summary>
    /// Registers a Postgres projection repository that inherits connection and schema from the EventStore module.
    /// The repository will use the same PostgreSQL connection and schema as its associated EventStore.
    /// </summary>
    /// <typeparam name="TKey">The projection key type</typeparam>
    /// <typeparam name="TState">The projected state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <param name="builder">The event store builder</param>
    /// <returns>The event store builder for chaining</returns>
    public static EventStoreModuleBuilder AddPostgresProjectionRepository<TKey, TState, TProjector>(
        this EventStoreModuleBuilder builder)
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        var moduleKey = builder.ModuleKey;

        // Register projector
        builder.Services.AddScoped<IProjector<TState>, TProjector>();
        builder.Services.AddScoped<TProjector>();

        // Register repository - resolve options from EventStore configuration at runtime
        builder.Services.AddScoped<IProjectionRepository<TKey, TState>>(sp =>
        {
            // Get EventStore options using IOptionsMonitor (singleton, safe to use here)
            var eventStoreOptionsMonitor = sp.GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>();
            var eventStoreOptions = eventStoreOptionsMonitor.Get(moduleKey);

            // Create projection options that inherit from EventStore
            var projectionOptions = new PostgresProjectionOptions { ConnectionString = eventStoreOptions.ConnectionString, Schema = eventStoreOptions.Schema };

            var logger = sp.GetRequiredService<ILogger<PostgresProjectionRepository<TKey, TState>>>();
            var tenantContext = sp.GetRequiredService<ITenantContext>();

            // Pass projector type to ensure consistent table naming with migrations
            return new PostgresProjectionRepository<TKey, TState>(
                Options.Create(projectionOptions),
                logger,
                tenantContext,
                typeof(TProjector));
        });

        // Register projection handler helper
        builder.Services.AddScoped<ProjectionHandler<TKey, TState>>();

        return builder;
    }
}