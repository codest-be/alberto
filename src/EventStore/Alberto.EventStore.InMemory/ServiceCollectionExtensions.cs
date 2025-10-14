using Microsoft.Extensions.DependencyInjection;

namespace Alberto.EventStore.InMemory;

public static class EventStoreBackendBuilderExtensions
{
    /// <summary>
    /// Configures this EventStore to use in-memory storage as the backend.
    /// Useful for development and testing scenarios.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore type</typeparam>
    /// <param name="builder">The backend builder</param>
    /// <returns>The backend builder for chaining</returns>
    public static EventStoreBackendBuilder<TEventStore> UseInMemory<TEventStore>(
        this EventStoreBackendBuilder<TEventStore> builder)
        where TEventStore : EventStoreFactory
    {
        // Register in-memory backend as a singleton (shared across scopes within module)
        builder.Services.AddKeyedSingleton<IEventStoreBackend, InMemoryEventStoreBackend>(
            builder.ModuleKey,
            (_, _) => new InMemoryEventStoreBackend());

        // Register the EventStore factory
        builder.RegisterEventStore();

        return builder;
    }

    /// <summary>
    /// Configures this EventStore to use a specific in-memory backend instance.
    /// Useful for testing scenarios where you want to control the backend instance.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore type</typeparam>
    /// <param name="builder">The backend builder</param>
    /// <param name="backend">The in-memory backend instance to use</param>
    /// <returns>The backend builder for chaining</returns>
    public static EventStoreBackendBuilder<TEventStore> UseInMemory<TEventStore>(
        this EventStoreBackendBuilder<TEventStore> builder,
        InMemoryEventStoreBackend backend)
        where TEventStore : EventStoreFactory
    {
        // Register the provided backend instance
        builder.Services.AddKeyedSingleton<IEventStoreBackend>(builder.ModuleKey, backend);

        // Register the EventStore factory
        builder.RegisterEventStore();

        return builder;
    }
}
