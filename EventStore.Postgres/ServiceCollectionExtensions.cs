using EventStore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Postgres;

public static class ServiceCollectionExtensions
{
    public static EventStoreBuilder AddPostgresEventStore(
        this EventStoreBuilder builder,
        Action<PostgresEventStoreOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.AddScoped<IEventStoreBackend, PostgresEventStoreBackend>();

        return builder;
    }
}