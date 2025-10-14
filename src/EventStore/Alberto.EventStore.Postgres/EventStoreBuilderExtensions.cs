using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.EventStore.Postgres;

public static class EventStoreBackendBuilderExtensions
{
    /// <summary>
    /// Configures this EventStore to use PostgreSQL as the storage backend
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore type</typeparam>
    /// <param name="builder">The backend builder</param>
    /// <param name="configureOptions">Configuration action for PostgreSQL options</param>
    /// <returns>The backend builder for chaining</returns>
    public static EventStoreBackendBuilder<TEventStore> UsePostgres<TEventStore>(
        this EventStoreBackendBuilder<TEventStore> builder,
        Action<PostgresEventStoreOptions> configureOptions)
        where TEventStore : EventStoreFactory
    {
        var options = new PostgresEventStoreOptions();
        configureOptions(options);

        // Register options with the module key
        builder.Services.Configure(builder.ModuleKey, configureOptions);

        // Register PostgreSQL backend implementation with the module key
        builder.Services.AddKeyedScoped<IEventStoreBackend, PostgresEventStoreBackend>(
            builder.ModuleKey,
            (provider, _) => new PostgresEventStoreBackend(
                Options.Create(
                    provider.GetRequiredService<IOptionsSnapshot<PostgresEventStoreOptions>>()
                        .Get(builder.ModuleKey)),
                provider.GetRequiredService<ILogger<PostgresEventStoreBackend>>()));

        // Register the EventStore factory
        builder.RegisterEventStore();

        return builder;
    }

    /// <summary>
    /// Legacy registration method for backward compatibility.
    /// NEW CODE SHOULD USE: services.AddEventStore().AddBackend{TEventStore}(moduleKey).UsePostgres(options)
    /// </summary>
    [Obsolete("Use services.AddEventStore().AddBackend<T>(moduleKey).UsePostgres(options) instead")]
    public static IServiceCollection AddPostgresEventStore<T>(
        this IServiceCollection services,
        Action<PostgresEventStoreOptions> configureOptions) where T : EventStoreFactory
    {
        var options = new PostgresEventStoreOptions();
        configureOptions(options);

        return services
            .AddEventStore()
            .AddBackend<T>(options.Schema)
            .UsePostgres(configureOptions)
            .Services;
    }
}
