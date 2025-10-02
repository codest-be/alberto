using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.EventStore.Postgres;

public static class EventStoreBuilderExtensions
{
    public static IServiceCollection AddPostgresEventStore<T>(
        this IServiceCollection services,
        Action<PostgresEventStoreOptions> configureOptions) where T : EventStore
    {
        var options = new PostgresEventStoreOptions();
        configureOptions(options);

        services.Configure(configureOptions);
        services.AddKeyedScoped<IEventStoreBackend, PostgresEventStoreBackend>(options.Schema);

        services.AddScoped<T>(sp => (T)Activator.CreateInstance(typeof(T), new EventStoreFactory(
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<IDiagnosticsEventListener>(),
            sp.GetRequiredKeyedService<IEventStoreBackend>(options.Schema)))!);

        return services;
    }
}