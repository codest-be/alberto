using Alberto.EventStore.InMemory.Subscriptions.Checkpoints;
using Alberto.EventStore.InMemory.Subscriptions.PoisonPills;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Polling;
using Alberto.EventStore.Subscriptions.Registration;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.InMemory;

public static class InMemorySubscriptionExtensions
{
    /// <summary>
    /// Adds EventStore with in-memory backend, telemetry, multi-tenancy AND subscription system in one call
    /// </summary>
    public static EventStoreModuleBuilder AddEventStoreWithInMemorySubscriptions<TTenantContext>(
        this IServiceCollection services,
        string moduleKey)
        where TTenantContext : class, ITenantContext
    {
        services
            .AddEventStore()
            .AddMultiTenancy<TTenantContext>();

        services.AddInMemoryEventStore();

        // Subscription system registration for background processing
        return services.WithInMemorySubscriptions(moduleKey);
    }

    /// <summary>
    /// Adds only the subscription system with in-memory storage
    /// </summary>
    public static EventStoreModuleBuilder WithInMemorySubscriptions(
        this IServiceCollection services,
        string moduleKey)
    {
        services.AddKeyedScoped<IMultiTenantEventStore>(moduleKey, (provider, _) =>
            provider.GetRequiredService<InMemoryEventStoreBackend>());

        // Register in-memory subscription infrastructure for this module
        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, _) =>
        {
            var logger = sp.GetRequiredService<ILogger<InMemoryCheckpointStore>>();
            return new InMemoryCheckpointStore(logger);
        });

        services.AddKeyedSingleton<IPoisonPillStore>(moduleKey, (sp, _) =>
        {
            var logger = sp.GetRequiredService<ILogger<InMemoryPoisonPillStore>>();
            return new InMemoryPoisonPillStore(logger);
        });

        return services.WithSubscriptions(moduleKey);
    }

    /// <summary>
    /// Core subscription services (storage-agnostic)
    /// </summary>
    private static EventStoreModuleBuilder WithSubscriptions(
        this IServiceCollection services,
        string moduleKey)
    {
        services.AddKeyedScoped<ConsumePipeline>(moduleKey, (sp, key) =>
        {
            var logger = sp.GetRequiredService<ILogger<ConsumePipeline>>();
            var pipeline = new ConsumePipeline(logger);

            // ALWAYS add TenantScopeFilter as the first filter to ensure tenant scoping
            var tenantContext = sp.GetRequiredService<ITenantContext>();
            var tenantScopeLogger = sp.GetRequiredService<ILogger<TenantScopeFilter>>();
            var tenantScopeFilter = new TenantScopeFilter(tenantContext, tenantScopeLogger);
            pipeline.AddFilter(tenantScopeFilter);

            // Add filters registered for this specific module
            var filters = sp.GetKeyedServices<IConsumeFilter>(key);
            foreach (var filter in filters)
            {
                pipeline.AddFilter(filter);
            }

            return pipeline;
        });

        services.AddKeyedSingleton<EventRouter>(moduleKey, (sp, key) =>
        {
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(key);
            var poisonPillStore = sp.GetRequiredKeyedService<IPoisonPillStore>(key);
            var logger = sp.GetRequiredService<ILogger<EventRouter>>();

            var router = new EventRouter(key!, checkpointStore, poisonPillStore, sp, logger);

            // Register handlers for this specific module
            using var scope = sp.CreateScope();
            var handlers = scope.ServiceProvider.GetKeyedServices<IEventHandler>(key);
            foreach (var handler in handlers)
            {
                var handlerType = handler.GetType();
                var subscriptionId = EventTypeDiscovery.GetSubscriptionId(handlerType);
                var supportedEventTypes = EventTypeDiscovery.DiscoverEventTypes(handlerType);
                var handlerLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(handlerType);

                var registration = new HandlerRegistration
                {
                    SubscriptionId = subscriptionId,
                    Handler = handler,
                    SupportedEventTypes = supportedEventTypes,
                    Logger = handlerLogger
                };

                router.RegisterHandler(registration);
            }

            return router;
        });

        return new EventStoreModuleBuilder(services, moduleKey);
    }
}