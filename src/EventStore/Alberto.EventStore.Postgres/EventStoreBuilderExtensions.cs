using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.EventStore.Postgres;

public static class EventStoreBuilderExtensions
{
    public static IServiceCollection AddPostgresEventStore<T>(
        this IServiceCollection services,
        Action<PostgresEventStoreOptions> configureOptions) where T : EventStoreFactory
    {
        PostgresEventStoreOptions options = new();
        configureOptions(options);

        services.Configure(options.Schema, configureOptions);

        services.AddKeyedScoped<IEventStoreBackend, PostgresEventStoreBackend>(options.Schema, (provider, _) =>
            new PostgresEventStoreBackend(
                Options.Create(provider.GetRequiredService<IOptionsSnapshot<PostgresEventStoreOptions>>()
                    .Get(options.Schema)),
                provider.GetRequiredService<ILogger<PostgresEventStoreBackend>>()));

        services.AddScoped<T>(sp => ((T)Activator.CreateInstance(typeof(T),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredKeyedService<IEventStoreBackend>(options.Schema),
            sp.GetRequiredKeyedService<IDiagnosticsEventListener>(options.Schema))!));


        return services;
    }
}