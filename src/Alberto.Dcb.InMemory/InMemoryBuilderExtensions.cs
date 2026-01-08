using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// Extension methods for configuring in-memory backend.
/// </summary>
public static class InMemoryBuilderExtensions
{
    /// <summary>
    /// Configures the module to use in-memory storage.
    /// Useful for testing and development.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder)
    {
        var moduleKey = builder.ModuleKey;

        // Create shared instances for the module
        var backend = new InMemoryEventStoreBackend();
        var eventStore = new InMemoryEventStore();
        var checkpointStore = new InMemoryCheckpointStore();

        builder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, backend);
        builder.Services.AddKeyedSingleton<IEventStore>(moduleKey, eventStore);
        builder.Services.AddKeyedSingleton<ICheckpointStore>(moduleKey, checkpointStore);

        return builder;
    }
}
