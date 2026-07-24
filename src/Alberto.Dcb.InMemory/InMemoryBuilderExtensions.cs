using Alberto.Dcb.Append;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// Extension methods for configuring in-memory backend.
/// </summary>
public static class InMemoryBuilderExtensions
{
    /// <summary>
    /// Uses an in-process event store for this module. Nothing is durable; use it for tests,
    /// samples and local development.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseBackend(new InMemoryBackendDescriptor());
    }

    /// <summary>
    /// Uses the in-process event store belonging to <paramref name="sharedModuleKey"/>, so
    /// several modules observe one event log. Useful when a test spans two modules.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="sharedModuleKey">The module key whose event store backend to share.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder, string sharedModuleKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedModuleKey);

        return builder.UseBackend(new InMemoryBackendDescriptor { SharedModuleKey = sharedModuleKey });
    }

    /// <summary>
    /// Registers the in-memory backend's services. Called by
    /// <see cref="InMemoryBackendDescriptor.Register"/> once the declaration is final.
    /// </summary>
    internal static void RegisterBackend(AlbertoModuleContext context, string? sharedModuleKey)
    {
        var services = context.Services;
        var moduleKey = context.ModuleKey;

        // Create shared instances for stores
        var checkpointStore = new InMemoryCheckpointStore();
        var deadLetterStore = new InMemoryDeadLetterStore();

        // Register append interceptor pipeline
        services.AddKeyedSingleton<IAppendInterceptorPipeline>(moduleKey, (sp, _) =>
        {
            var interceptors = sp.GetKeyedServices<IAppendInterceptor>(moduleKey);
            return new AppendInterceptorPipeline(interceptors);
        });

        if (sharedModuleKey is not null)
        {
            // Resolve the shared backend from the other module
            services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
            {
                var sharedBackend = sp.GetRequiredKeyedService<IEventStoreBackend>(sharedModuleKey);
                var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
                return new InterceptingEventStoreBackend(sharedBackend, pipeline);
            });
        }
        else
        {
            // Register event store backend with intercepting decorator
            services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
            {
                var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
                var rawBackend = new InMemoryEventStoreBackend(timeProvider);
                var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
                return new InterceptingEventStoreBackend(rawBackend, pipeline);
            });
        }

        // Register event store (uses intercepting backend)
        services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, key) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
            var eventStore = new InMemoryEventStore(backend);
            RegisterInlineProjections(sp, key, eventStore);
            RegisterPostAppendHandlers(sp, key, eventStore);
            return eventStore;
        });

        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, checkpointStore);
        services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, deadLetterStore);
    }

    private static void RegisterPostAppendHandlers(IServiceProvider sp, object? key, InMemoryEventStore eventStore)
    {
        foreach (var handler in sp.GetKeyedServices<IPostAppendHandler>(key))
            eventStore.RegisterPostAppendHandler(handler);
    }

    private static void RegisterInlineProjections(IServiceProvider sp, object? key, InMemoryEventStore eventStore)
    {
        foreach (var projection in sp.GetKeyedServices<IInlineProjection>(key))
            eventStore.RegisterInlineProjection(projection);
    }
}
