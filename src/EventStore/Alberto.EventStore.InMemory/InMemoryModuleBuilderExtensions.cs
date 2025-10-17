using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.InMemory.Subscriptions.Checkpoints;
using Alberto.EventStore.InMemory.Subscriptions.PoisonPills;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Polling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.InMemory;

/// <summary>
/// Extension methods for configuring InMemory backend in ModuleBuilder
/// </summary>
public static class InMemoryModuleBuilderExtensions
{
    /// <summary>
    /// Configures this EventStore module to use in-memory storage as the backend.
    /// Useful for development and testing scenarios.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore type</typeparam>
    /// <param name="moduleBuilder">The module builder</param>
    /// <returns>The module builder for chaining</returns>
    public static ModuleBuilder<TEventStore> WithInMemory<TEventStore>(
        this ModuleBuilder<TEventStore> moduleBuilder)
        where TEventStore : EventStoreFactory
    {
        // Register in-memory backend as a singleton (shared across scopes within module)
        moduleBuilder.Services.AddKeyedSingleton<IEventStoreBackend, InMemoryEventStoreBackend>(
            moduleBuilder.ModuleKey,
            (sp, _) =>
            {
                var logger = sp.GetRequiredService<ILogger<InMemoryEventStoreBackend>>();
                return new InMemoryEventStoreBackend(logger);
            });

        moduleBuilder.Services.AddKeyedSingleton<IMultiTenantEventStore, InMemoryEventStoreBackend>(
            moduleBuilder.ModuleKey,
            (sp, _) =>
            {
                var logger = sp.GetRequiredService<ILogger<InMemoryEventStoreBackend>>();
                return new InMemoryEventStoreBackend(logger);
            });

        // Register subscription infrastructure (checkpoint and poison pill stores)
        RegisterInMemorySubscriptionInfrastructure(moduleBuilder);

        // Register the EventStore factory
        moduleBuilder.RegisterEventStore();

        // Mark backend as configured
        moduleBuilder.MarkBackendConfigured();

        return moduleBuilder;
    }

    /// <summary>
    /// Configures this EventStore module to use a specific in-memory backend instance.
    /// Useful for testing scenarios where you want to control the backend instance.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore type</typeparam>
    /// <param name="moduleBuilder">The module builder</param>
    /// <param name="backend">The in-memory backend instance to use</param>
    /// <returns>The module builder for chaining</returns>
    public static ModuleBuilder<TEventStore> WithInMemory<TEventStore>(
        this ModuleBuilder<TEventStore> moduleBuilder,
        InMemoryEventStoreBackend backend)
        where TEventStore : EventStoreFactory
    {
        // Register the provided backend instance
        moduleBuilder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleBuilder.ModuleKey, backend);

        moduleBuilder.Services.AddKeyedSingleton<IMultiTenantEventStore>(moduleBuilder.ModuleKey, backend);

        // Register subscription infrastructure
        RegisterInMemorySubscriptionInfrastructure(moduleBuilder);

        // Register the EventStore factory
        moduleBuilder.RegisterEventStore();

        // Mark backend as configured
        moduleBuilder.MarkBackendConfigured();

        return moduleBuilder;
    }

    private static void RegisterInMemorySubscriptionInfrastructure<TEventStore>(
        ModuleBuilder<TEventStore> moduleBuilder)
        where TEventStore : EventStoreFactory
    {
        var services = moduleBuilder.Services;
        var moduleKey = moduleBuilder.ModuleKey;

        // Register checkpoint store
        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, _) =>
        {
            var logger = sp.GetRequiredService<ILogger<InMemoryCheckpointStore>>();
            return new InMemoryCheckpointStore(logger);
        });

        // Register poison pill store with caching
        services.AddKeyedSingleton<IPoisonPillStore>(moduleKey, (sp, _) =>
        {
            var innerLogger = sp.GetRequiredService<ILogger<InMemoryPoisonPillStore>>();
            var cachedLogger = sp.GetRequiredService<ILogger<CachedPoisonPillStore>>();

            // Create inner in-memory poison pill store
            var innerStore = new InMemoryPoisonPillStore(innerLogger);

            // Try to get polling options (may not be configured if no subscriptions are set up)
            var pollingOptions = sp.GetKeyedService<PollingOptions>(moduleKey);
            var cacheSize = pollingOptions?.PoisonPillCacheSize ?? 10000;

            // Wrap with caching for consistency with PostgreSQL implementation
            return new CachedPoisonPillStore(
                innerStore,
                cacheSize,
                cachedLogger);
        });

        // Register default no-op trace context provider (can be overridden by WithTelemetry)
        services.TryAddSingleton<ITraceContextProvider, NoopTraceContextProvider>();

        // Register default filters that are always included
        RegisterDefaultFilters(services, moduleKey);
    }

    private static void RegisterDefaultFilters(IServiceCollection services, string moduleKey)
    {
        // TenantScopeFilter is always added first to ensure tenant context is set
        services.AddKeyedScoped<TenantScopeFilter>(moduleKey, (sp, _) =>
        {
            var tenantContext = sp.GetRequiredService<ITenantContext>();
            var logger = sp.GetRequiredService<ILogger<TenantScopeFilter>>();
            return new TenantScopeFilter(tenantContext, logger);
        });

        // Register TelemetryConsumeFilter for channel subscriptions (synchronous)
        services.AddKeyedScoped<TelemetryConsumeFilter>($"{moduleKey}:channel", (sp, _) =>
        {
            var traceContextProvider = sp.GetRequiredService<ITraceContextProvider>();
            return new TelemetryConsumeFilter(traceContextProvider, isSynchronous: true);
        });

        // Register TelemetryConsumeFilter for polling subscriptions (asynchronous)
        services.AddKeyedScoped<TelemetryConsumeFilter>($"{moduleKey}:polling", (sp, _) =>
        {
            var traceContextProvider = sp.GetRequiredService<ITraceContextProvider>();
            return new TelemetryConsumeFilter(traceContextProvider, isSynchronous: false);
        });
    }
}