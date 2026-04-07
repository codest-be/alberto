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
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var rawBackend = new InMemoryEventStoreBackend(timeProvider);
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(rawBackend, pipeline);
        });

        // Register event store (uses intercepting backend)
        builder.Services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, key) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
            var eventStore = new InMemoryEventStore(backend);
            RegisterPostAppendHandlers(sp, key, eventStore);
            return eventStore;
        });

        builder.Services.AddKeyedSingleton<ICheckpointStore>(moduleKey, checkpointStore);
        builder.Services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, deadLetterStore);

        return builder;
    }

    /// <summary>
    /// Configures the module to share the in-memory event store backend from another module.
    /// The checkpoint and dead letter stores are independent, so each consumer tracks its own position.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="sharedModuleKey">The module key whose <see cref="IEventStoreBackend"/> to share.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder, string sharedModuleKey)
    {
        var moduleKey = builder.ModuleKey;

        var checkpointStore = new InMemoryCheckpointStore();
        var deadLetterStore = new InMemoryDeadLetterStore();

        builder.Services.AddKeyedSingleton<IAppendInterceptorPipeline>(moduleKey, (sp, _) =>
        {
            var interceptors = sp.GetKeyedServices<IAppendInterceptor>(moduleKey);
            return new AppendInterceptorPipeline(interceptors);
        });

        // Resolve the shared backend from the other module
        builder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var sharedBackend = sp.GetRequiredKeyedService<IEventStoreBackend>(sharedModuleKey);
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(sharedBackend, pipeline);
        });

        // Pass-through event store using the shared backend
        builder.Services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, key) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
            var eventStore = new InMemoryEventStore(backend);
            RegisterPostAppendHandlers(sp, key, eventStore);
            return eventStore;
        });

        builder.Services.AddKeyedSingleton<ICheckpointStore>(moduleKey, checkpointStore);
        builder.Services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, deadLetterStore);

        return builder;
    }

    private static void RegisterPostAppendHandlers(IServiceProvider sp, object? key, InMemoryEventStore eventStore)
    {
        foreach (var handler in sp.GetKeyedServices<IPostAppendHandler>(key))
            eventStore.RegisterPostAppendHandler(handler);
    }
}
