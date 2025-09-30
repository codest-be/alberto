using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Postgres;

public static class EventStoreBuilderExtensions
{
    public static EventStoreBuilder AddPostgresEventStore(
        this EventStoreBuilder builder,
        Action<PostgresEventStoreOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.AddScoped<IEventStoreBackend, PostgresEventStoreBackend>();
        builder.Services.AddScoped<IEventStoreBackendFactory, PostgresEventStoreBackendFactory>(sp => new PostgresEventStoreBackendFactory(sp, new SchemaContext()));

        return builder;
    }

    public static EventStoreBuilder AddPostgresEventStore(
        this EventStoreBuilder builder,
        string schema,
        Action<PostgresEventStoreOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        
        builder.Services.AddScoped<ISchemaContext, SchemaContext>();
        builder.Services.AddKeyedScoped<IEventStoreBackend, PostgresEventStoreBackend>(schema);
        builder.Services.AddScoped<IEventStoreBackendFactory, PostgresEventStoreBackendFactory>();

        return builder;
    }
}