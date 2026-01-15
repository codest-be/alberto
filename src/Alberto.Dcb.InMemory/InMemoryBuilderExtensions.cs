using Alberto.Dcb.Append;
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

        // Create shared instances for stores
        var checkpointStore = new InMemoryCheckpointStore();
        var deadLetterStore = new InMemoryDeadLetterStore();

        // Register append interceptor pipeline
        builder.Services.AddKeyedSingleton<IAppendInterceptorPipeline>(moduleKey, (sp, _) =>
        {
            var interceptors = sp.GetKeyedServices<IAppendInterceptor>(moduleKey);
            return new AppendInterceptorPipeline(interceptors);
        });

        // Register event store backend with intercepting decorator
        builder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var rawBackend = new InMemoryEventStoreBackend();
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(rawBackend, pipeline);
        });

        // Register event store (uses intercepting backend)
        builder.Services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, _) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            return new InMemoryEventStore(backend);
        });

        builder.Services.AddKeyedSingleton<ICheckpointStore>(moduleKey, checkpointStore);
        builder.Services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, deadLetterStore);

        return builder;
    }
}
