using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres.Subscriptions.Checkpoints;
using Alberto.EventStore.Postgres.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Polling;
using Alberto.EventStore.Subscriptions.Registration;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.EventStore.Postgres;

public static class PostgresSubscriptionExtensions
{
    /// <summary>
    /// Adds EventStore with PostgreSQL backend, telemetry, multi-tenancy AND subscription system in one call
    /// </summary>
    public static EventStoreModuleBuilder AddEventStoreWithPostgresSubscriptions<TEventStore, TTenantContext>(
        this IServiceCollection services,
        string moduleKey,
        Action<PostgresEventStoreOptions> configureOptions)
        where TEventStore : EventStoreFactory
        where TTenantContext : class, ITenantContext
    {
        services
            .AddEventStore()
            .AddMultiTenancy<TTenantContext>();

        services.AddPostgresEventStore<TEventStore>(configureOptions);

        // Subscription system registration for background processing
        return services.WithPostgresSubscriptions(moduleKey);
    }

    /// <summary>
    /// Adds only the subscription system with PostgreSQL storage
    /// </summary>
    public static EventStoreModuleBuilder WithPostgresSubscriptions(
        this IServiceCollection services,
        string moduleKey)
    {
        services.AddKeyedScoped<IMultiTenantEventStore>(moduleKey, (provider, _) => new PostgresEventStoreBackend(
            Options.Create(provider.GetRequiredService<IOptionsSnapshot<PostgresEventStoreOptions>>().Get(moduleKey)),
            provider.GetRequiredService<ILogger<PostgresEventStoreBackend>>()));

        // Register PostgreSQL-specific subscription infrastructure for this module
        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, key) =>
        {
            var keyName = key?.ToString() ?? moduleKey;
            var options = sp.GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>().Get(keyName);
            var logger = sp.GetRequiredService<ILogger<PostgresCheckpointStore>>();
            return new PostgresCheckpointStore(options.ConnectionString, options.Schema, logger);
        });

        services.AddKeyedSingleton<IPoisonPillStore>(moduleKey, (sp, key) =>
        {
            var keyName = key?.ToString() ?? moduleKey;
            var options = sp
                .GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>()
                .Get(keyName);
            var logger = sp.GetRequiredService<ILogger<PostgresPoisonPillStore>>();
            return new PostgresPoisonPillStore(options.ConnectionString, options.Schema, logger);
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