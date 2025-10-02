using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Postgres;

public static class EventStoreBuilderExtensions
{
    public static EventStoreBuilder AddPostgresEventStore(
        this EventStoreBuilder builder,
        Action<PostgresEventStoreOptions> configureOptions)
    {
        var options = new PostgresEventStoreOptions();
        configureOptions(options);
        
        builder.Services.Configure(configureOptions);
        builder.Services.AddScoped<ISchemaContext, SchemaContext>();
        builder.Services.AddKeyedScoped<IEventStoreBackend, PostgresEventStoreBackend>(options.Schema);
        builder.Services.AddScoped<IEventStoreBackendFactory, PostgresEventStoreBackendFactory>();

        return builder;
    }
}