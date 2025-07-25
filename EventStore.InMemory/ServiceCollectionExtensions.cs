using Microsoft.Extensions.DependencyInjection;

namespace EventStore.InMemory;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryEventStore(this IServiceCollection services)
    {
        services.AddScoped<EventStore>();
        services.AddSingleton<IEventStoreBackend, InMemoryEventStoreBackend>();
        return services;
    }

    public static IServiceCollection AddTestingEventStore(this IServiceCollection services,
        InMemoryEventStoreBackend backend)
    {
        services.AddSingleton<IEventStoreBackend>(backend);
        return services;
    }
}