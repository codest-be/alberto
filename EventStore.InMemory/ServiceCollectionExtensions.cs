using EventStore.MultiTenant;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.InMemory;

public static class ServiceCollectionExtensions
{
    public static EventStoreBuilder AddInMemoryEventStore(this EventStoreBuilder builder)
    {
        builder.Services.AddScoped<EventStore>();
        builder.Services.AddSingleton<IEventStoreBackend, InMemoryEventStoreBackend>();
        builder.Services.AddSingleton<IEventStoreBackendFactory, InMemoryEventStoreBackendFactory>();
        return builder;
    }

    public static EventStoreBuilder AddTestingEventStore(this EventStoreBuilder builder,
        InMemoryEventStoreBackend backend)
    {
        builder.Services.AddSingleton<IEventStoreBackend>(backend);
        builder.Services.AddSingleton<IEventStoreBackendFactory, InMemoryEventStoreBackendFactory>();
        return builder;
    }
}