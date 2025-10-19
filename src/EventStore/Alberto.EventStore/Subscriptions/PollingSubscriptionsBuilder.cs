using System.Reflection;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Alberto.EventStore.Subscriptions.DistributedLocking;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Polling;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions;

/// <summary>
/// Builder for configuring polling-based event subscriptions
/// </summary>
/// <typeparam name="TEventStore">The EventStore factory type</typeparam>
public class PollingSubscriptionsBuilder<TEventStore> where TEventStore : EventStoreFactory
{
    private readonly List<Type> _filterTypes = [];
    private readonly string _moduleKey;
    private readonly IServiceCollection _services;
    private PollingOptions _pollingOptions = new();

    internal PollingSubscriptionsBuilder(IServiceCollection services, string moduleKey)
    {
        _services = services;
        _moduleKey = moduleKey;
    }

    /// <summary>
    /// Gets the service collection for advanced extension scenarios
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Gets the module key for keyed service registration
    /// </summary>
    public string ModuleKey => _moduleKey;

    /// <summary>
    /// Configures polling options for this subscription module
    /// </summary>
    /// <param name="configure">Configuration action for polling options</param>
    /// <returns>The builder for chaining</returns>
    public PollingSubscriptionsBuilder<TEventStore> Configure(Action<PollingOptions> configure)
    {
        configure(_pollingOptions);
        return this;
    }

    /// <summary>
    /// Adds a filter to the event processing pipeline
    /// </summary>
    /// <typeparam name="TFilter">The filter type</typeparam>
    /// <returns>The builder for chaining</returns>
    public PollingSubscriptionsBuilder<TEventStore> WithFilter<TFilter>()
        where TFilter : class, IConsumeFilter
    {
        _filterTypes.Add(typeof(TFilter));
        _services.AddKeyedScoped<TFilter>(_moduleKey);
        return this;
    }


    /// <summary>
    /// Adds a custom event handler to the subscription pipeline
    /// </summary>
    /// <typeparam name="THandler">The handler type implementing IHandleEvent</typeparam>
    /// <returns>The builder for chaining</returns>
    public PollingSubscriptionsBuilder<TEventStore> AddHandler<THandler>()
        where THandler : class, IEventHandler
    {
        // Register the handler with module key
        _services.AddKeyedScoped<THandler>(_moduleKey);

        // Register as IEventHandler for discovery within this module
        _services.AddKeyedScoped<IEventHandler>(_moduleKey, (sp, _) =>
            sp.GetRequiredKeyedService<THandler>(_moduleKey));

        return this;
    }

    /// <summary>
    /// Discovers and registers all handlers from the specified assembly
    /// </summary>
    /// <param name="assembly">Assembly to scan for handlers</param>
    /// <returns>The builder for chaining</returns>
    public PollingSubscriptionsBuilder<TEventStore> ScanAssembly(Assembly assembly)
    {
        var handlerTypes = EventTypeDiscovery.DiscoverHandlerTypes(assembly);

        foreach (var handlerType in handlerTypes)
        {
            // Register the handler with module key
            _services.AddKeyedScoped(handlerType, _moduleKey);

            // Register as IEventHandler for discovery within this module
            _services.AddKeyedScoped<IEventHandler>(_moduleKey, (sp, _) =>
                (IEventHandler)sp.GetRequiredKeyedService(handlerType, _moduleKey));
        }

        return this;
    }

    /// <summary>
    /// Builds and registers the subscription infrastructure (internal use)
    /// </summary>
    internal void Build()
    {
        // Register polling options
        _services.AddKeyedSingleton(_moduleKey, (_, _) => _pollingOptions);

        // Register ConsumePipeline with filters
        _services.AddKeyedScoped<ConsumePipeline>(_moduleKey, (sp, _) =>
        {
            var logger = sp.GetRequiredService<ILogger<ConsumePipeline>>();
            var pipeline = new ConsumePipeline(logger);

            // Add default filters first (TenantScopeFilter and TelemetryConsumeFilter)
            // These are registered by the backend (Postgres/InMemory) extensions
            var tenantScopeFilter = sp.GetKeyedService<TenantScopeFilter>(_moduleKey);
            if (tenantScopeFilter != null)
            {
                pipeline.AddFilter(tenantScopeFilter);
            }

            // Use polling-specific telemetry filter (asynchronous mode) if available,
            // otherwise fall back to module-keyed filter for backward compatibility
            var telemetryFilter = sp.GetKeyedService<TelemetryConsumeFilter>($"{_moduleKey}:polling")
                                  ?? sp.GetKeyedService<TelemetryConsumeFilter>(_moduleKey);
            if (telemetryFilter != null)
            {
                pipeline.AddFilter(telemetryFilter);
            }

            // Add user-defined filters in order
            foreach (var filterType in _filterTypes)
            {
                var filter = (IConsumeFilter)sp.GetRequiredKeyedService(filterType, _moduleKey);
                pipeline.AddFilter(filter);
            }

            // Add any additional IConsumeFilter services registered with this module key
            // This allows test fixtures and other code to add filters directly
            var additionalFilters = sp.GetKeyedServices<IConsumeFilter>(_moduleKey);
            foreach (var filter in additionalFilters)
            {
                // Only add if not already added (avoid duplicates from WithFilter<> registrations)
                if (!_filterTypes.Contains(filter.GetType()))
                {
                    pipeline.AddFilter(filter);
                }
            }

            return pipeline;
        });

        // Register EventRouter
        _services.AddKeyedSingleton<EventRouter>(_moduleKey, (sp, _) =>
        {
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(_moduleKey);
            var poisonPillStore = sp.GetRequiredKeyedService<IPoisonPillStore>(_moduleKey);
            var logger = sp.GetRequiredService<ILogger<EventRouter>>();
            var metrics = sp.GetRequiredService<IMetricsRecorder>();

            var router = new EventRouter(
                _moduleKey,
                checkpointStore,
                poisonPillStore,
                sp,
                logger,
                metrics,
                _pollingOptions.MaxRetries,
                _pollingOptions.RetryDelayMs
            );

            // Discover and register all handlers
            var handlers = sp.GetKeyedServices<IEventHandler>(_moduleKey);
            foreach (var handler in handlers)
            {
                var subscriptionId = GetSubscriptionId(handler);
                var supportedEventTypes = GetSupportedEventTypes(handler);
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var handlerLogger = loggerFactory.CreateLogger(handler.GetType());

                router.RegisterHandler(new HandlerRegistration
                {
                    SubscriptionId = subscriptionId, Handler = handler, SupportedEventTypes = supportedEventTypes, Logger = handlerLogger
                });
            }

            return router;
        });

        // Register SubscriptionPollingService as hosted service
        _services.AddSingleton<IHostedService>(sp => new SubscriptionPollingService(
            _moduleKey,
            sp.GetRequiredKeyedService<EventRouter>(_moduleKey),
            sp.GetRequiredKeyedService<PollingOptions>(_moduleKey),
            sp.GetRequiredKeyedService<IDistributedLock>(_moduleKey),
            sp,
            sp.GetRequiredService<ILogger<SubscriptionPollingService>>(),
            sp.GetRequiredService<IMetricsRecorder>()
        ));
    }

    private static string GetSubscriptionId(IEventHandler handler)
    {
        var attribute = handler.GetType().GetCustomAttribute<SubscriptionAttribute>();
        if (attribute != null)
            return attribute.SubscriptionId;

        // Fallback to type name
        return handler.GetType().Name;
    }

    private static HashSet<string> GetSupportedEventTypes(IEventHandler handler)
    {
        var handlerType = handler.GetType();
        var handleInterfaces = handlerType
            .GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHandleEvent<>));

        var eventTypes = new HashSet<string>();

        foreach (var handleInterface in handleInterfaces)
        {
            var eventType = handleInterface.GetGenericArguments()[0];
            var eventTypeObj = EventType.GetEventType(eventType);
            if (eventTypeObj != null)
            {
                eventTypes.Add(eventTypeObj.Id);
            }
        }

        return eventTypes;
    }
}