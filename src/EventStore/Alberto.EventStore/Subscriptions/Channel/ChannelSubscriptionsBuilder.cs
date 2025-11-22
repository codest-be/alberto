using System.Reflection;
using System.Threading.Channels;
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

namespace Alberto.EventStore.Subscriptions.Channel;

/// <summary>
/// Tracks a handler registration with its subscription mode
/// </summary>
/// <param name="HandlerType">The handler type</param>
/// <param name="Mode">The subscription mode for this handler</param>
internal record HandlerModeRegistration(Type HandlerType, SubscriptionMode Mode);

/// <summary>
/// Builder for configuring event subscriptions supporting Sync (channel-based), Async (polling-based), and Hybrid modes
/// </summary>
/// <typeparam name="TEventStore">The EventStore factory type</typeparam>
public class ChannelSubscriptionsBuilder<TEventStore> where TEventStore : EventStoreFactory
{
    private readonly ChannelOptions _channelOptions = new();
    private readonly List<Type> _filterTypes = [];
    private readonly List<HandlerModeRegistration> _handlers = [];
    private readonly string _moduleKey;
    private readonly PollingOptions _pollingOptions = new();
    private readonly IServiceCollection _services;

    internal ChannelSubscriptionsBuilder(IServiceCollection services, string moduleKey)
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
    /// Configures options for Sync and Hybrid subscription modes (channel-based event delivery)
    /// </summary>
    /// <param name="configure">Configuration action for channel options</param>
    /// <returns>The builder for chaining</returns>
    public ChannelSubscriptionsBuilder<TEventStore> ConfigureSync(Action<ChannelOptions> configure)
    {
        configure(_channelOptions);
        return this;
    }

    /// <summary>
    /// Configures options for Async and Hybrid subscription modes (polling-based event delivery)
    /// </summary>
    /// <param name="configure">Configuration action for polling options</param>
    /// <returns>The builder for chaining</returns>
    public ChannelSubscriptionsBuilder<TEventStore> ConfigureAsync(Action<PollingOptions> configure)
    {
        configure(_pollingOptions);
        return this;
    }

    /// <summary>
    /// Adds a filter to the event processing pipeline
    /// </summary>
    /// <typeparam name="TFilter">The filter type</typeparam>
    /// <returns>The builder for chaining</returns>
    public ChannelSubscriptionsBuilder<TEventStore> WithFilter<TFilter>()
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
    /// <param name="mode">Subscription mode for this handler (Sync, Async, or Hybrid). Default: Sync</param>
    /// <returns>The builder for chaining</returns>
    public ChannelSubscriptionsBuilder<TEventStore> AddHandler<THandler>(SubscriptionMode mode = SubscriptionMode.Sync)
        where THandler : class, IEventHandler
    {
        // Track handler with its mode
        _handlers.Add(new HandlerModeRegistration(typeof(THandler), mode));

        // Register the handler with module key
        _services.AddKeyedScoped<THandler>(_moduleKey);

        // Register as IEventHandler for discovery within this module
        _services.AddKeyedScoped<IEventHandler>(_moduleKey, (sp, _) =>
            sp.GetRequiredKeyedService<THandler>(_moduleKey));

        return this;
    }

    /// <summary>
    /// Adds a custom channel consumer for external integrations (SignalR, WebSocket, etc.)
    /// </summary>
    /// <typeparam name="TConsumer">The consumer type implementing IChannelConsumer</typeparam>
    /// <returns>The builder for chaining</returns>
    public ChannelSubscriptionsBuilder<TEventStore> AddConsumer<TConsumer>()
        where TConsumer : class, IChannelConsumer
    {
        _services.AddKeyedScoped<TConsumer>(_moduleKey);

        // Register as IChannelConsumer for discovery within this module
        _services.AddKeyedScoped<IChannelConsumer>(_moduleKey, (sp, _) =>
            sp.GetRequiredKeyedService<TConsumer>(_moduleKey));

        return this;
    }

    /// <summary>
    /// Discovers and registers all handlers from the specified assembly
    /// </summary>
    /// <param name="assembly">Assembly to scan for handlers</param>
    /// <returns>The builder for chaining</returns>
    public ChannelSubscriptionsBuilder<TEventStore> ScanAssembly(Assembly assembly)
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
        // Register channel options
        _services.AddKeyedSingleton(_moduleKey, (_, _) => _channelOptions);

        // Determine which infrastructure to build based on handler modes
        var needsChannel = _handlers.Any(h => h.Mode is SubscriptionMode.Sync or SubscriptionMode.Hybrid);
        var needsPolling = _handlers.Any(h => h.Mode is SubscriptionMode.Async or SubscriptionMode.Hybrid);

        // Get handlers for each mode
        var channelHandlers = _handlers
            .Where(h => h.Mode is SubscriptionMode.Sync or SubscriptionMode.Hybrid)
            .ToList();

        var pollingHandlers = _handlers
            .Where(h => h.Mode is SubscriptionMode.Async or SubscriptionMode.Hybrid)
            .ToList();

        // Build infrastructure as needed
        if (needsChannel)
        {
            BuildChannelInfrastructure(channelHandlers);
        }

        if (needsPolling)
        {
            BuildPollingInfrastructure(pollingHandlers);
        }
    }

    private void BuildChannelInfrastructure(List<HandlerModeRegistration> channelHandlers)
    {
        var channelRouterKey = $"{_moduleKey}:channel";

        // Create bounded channel for event distribution
        // Drop oldest events when full to prevent blocking writes and maintain low latency
        var channel = System.Threading.Channels.Channel.CreateBounded<GlobalEventEnvelope>(
            new BoundedChannelOptions(_channelOptions.BoundedCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest // Drop old events instead of blocking writes
            });

        // Register channel reader for the service
        _services.AddKeyedSingleton(_moduleKey, (_, _) => channel.Reader);

        // Register channel writer for the notification hub
        _services.AddKeyedSingleton(_moduleKey, (_, _) => channel.Writer);

        // Register the channel writer with the ChannelSubscriptionRegistry
        _services.AddSingleton<IHostedService>(sp =>
        {
            var registry = sp.GetRequiredService<ChannelSubscriptionRegistry>();
            var writer = sp.GetRequiredKeyedService<ChannelWriter<GlobalEventEnvelope>>(_moduleKey);
            var eventRouter = sp.GetRequiredKeyedService<EventRouter>(channelRouterKey);
            var logger = sp.GetRequiredService<ILogger<ChannelRegistrationService>>();

            // Extract event types from channel handlers
            var eventTypes = GetEventTypesFromRouter(eventRouter);

            logger.LogInformation(
                "Creating ChannelRegistrationService for module '{ModuleKey}' with {EventTypeCount} event types",
                _moduleKey,
                eventTypes?.Count ?? 0);

            return new ChannelRegistrationService(registry, writer, _moduleKey, eventTypes, logger);
        });

        // Register ConsumePipeline with filters for channel
        _services.AddKeyedScoped<ConsumePipeline>(channelRouterKey, (sp, _) =>
        {
            var logger = sp.GetRequiredService<ILogger<ConsumePipeline>>();
            var pipeline = new ConsumePipeline(logger);

            // Add default filters first (TenantScopeFilter and TelemetryConsumeFilter)
            var tenantScopeFilter = sp.GetKeyedService<TenantScopeFilter>(_moduleKey);
            if (tenantScopeFilter != null)
            {
                pipeline.AddFilter(tenantScopeFilter);
            }

            // Use channel-specific telemetry filter (synchronous mode)
            var telemetryFilter = sp.GetKeyedService<TelemetryConsumeFilter>(channelRouterKey);
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

            return pipeline;
        });

        // Register EventRouter for channel handlers only
        _services.AddKeyedSingleton<EventRouter>(channelRouterKey, (sp, _) =>
        {
            // Channel router uses ephemeral checkpoints (in-memory only, not persisted to DB)
            // This prevents race conditions with polling router in Hybrid mode
            // On restart, polling router will catch up on any missed events
            var checkpointStore = new EphemeralCheckpointStore();
            var poisonPillStore = sp.GetRequiredKeyedService<IPoisonPillStore>(_moduleKey);
            var logger = sp.GetRequiredService<ILogger<EventRouter>>();
            var metrics = sp.GetRequiredService<IMetricsRecorder>();
            var builderLogger = sp.GetRequiredService<ILogger<ChannelSubscriptionsBuilder<TEventStore>>>();

            var router = new EventRouter(
                _moduleKey,
                channelRouterKey,
                checkpointStore,
                poisonPillStore,
                sp,
                logger,
                metrics,
                _channelOptions.MaxRetries,
                _channelOptions.RetryDelayMs
            );

            builderLogger.LogInformation(
                "Registering {HandlerCount} handlers for channel router (module: {ModuleKey})",
                channelHandlers.Count,
                _moduleKey);

            // Register only channel handlers
            using var scope = sp.CreateScope();
            foreach (var handlerReg in channelHandlers)
            {
                var handler =
                    (IEventHandler)scope.ServiceProvider.GetRequiredKeyedService(handlerReg.HandlerType, _moduleKey);
                var subscriptionId = GetSubscriptionId(handler);
                var supportedEventTypes = GetSupportedEventTypes(handler);
                var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
                var handlerLogger = loggerFactory.CreateLogger(handler.GetType());

                builderLogger.LogInformation(
                    "Registering channel handler: {HandlerType} (subscription: {SubscriptionId}, mode: {Mode}, events: {EventTypes})",
                    handlerReg.HandlerType.Name,
                    subscriptionId,
                    handlerReg.Mode,
                    string.Join(", ", supportedEventTypes));

                router.RegisterHandler(new HandlerRegistration
                {
                    SubscriptionId = subscriptionId, HandlerType = handler.GetType(),
                    SupportedEventTypes = supportedEventTypes, Logger = handlerLogger
                });
            }

            return router;
        });

        // Register ChannelSubscriptionService as hosted service
        _services.AddSingleton<IHostedService>(sp =>
        {
            var eventRouter = sp.GetRequiredKeyedService<EventRouter>(channelRouterKey);
            var channelReader = sp.GetRequiredKeyedService<ChannelReader<GlobalEventEnvelope>>(_moduleKey);
            var consumers = sp.GetKeyedServices<IChannelConsumer>(_moduleKey);
            var metrics = sp.GetRequiredService<IMetricsRecorder>();
            var logger = sp.GetRequiredService<ILogger<ChannelSubscriptionService>>();

            return new ChannelSubscriptionService(
                _moduleKey,
                eventRouter,
                channelReader,
                _channelOptions,
                consumers,
                metrics,
                logger
            );
        });
    }

    private void BuildPollingInfrastructure(List<HandlerModeRegistration> pollingHandlers)
    {
        var pollingRouterKey = $"{_moduleKey}:polling";

        // Register polling options
        _services.AddKeyedSingleton(_moduleKey, (_, _) => _pollingOptions);

        // Register ConsumePipeline with filters for polling
        _services.AddKeyedScoped<ConsumePipeline>(pollingRouterKey, (sp, _) =>
        {
            var logger = sp.GetRequiredService<ILogger<ConsumePipeline>>();
            var pipeline = new ConsumePipeline(logger);

            // Add default filters first (TenantScopeFilter and TelemetryConsumeFilter)
            var tenantScopeFilter = sp.GetKeyedService<TenantScopeFilter>(_moduleKey);
            if (tenantScopeFilter != null)
            {
                pipeline.AddFilter(tenantScopeFilter);
            }

            // Use polling-specific telemetry filter (asynchronous mode)
            var telemetryFilter = sp.GetKeyedService<TelemetryConsumeFilter>(pollingRouterKey);
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

            return pipeline;
        });

        // Register EventRouter for polling handlers only
        _services.AddKeyedSingleton<EventRouter>(pollingRouterKey, (sp, _) =>
        {
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(_moduleKey);
            var poisonPillStore = sp.GetRequiredKeyedService<IPoisonPillStore>(_moduleKey);
            var logger = sp.GetRequiredService<ILogger<EventRouter>>();
            var metrics = sp.GetRequiredService<IMetricsRecorder>();
            var builderLogger = sp.GetRequiredService<ILogger<ChannelSubscriptionsBuilder<TEventStore>>>();

            var router = new EventRouter(
                _moduleKey,
                pollingRouterKey,
                checkpointStore,
                poisonPillStore,
                sp,
                logger,
                metrics,
                _pollingOptions.MaxRetries,
                _pollingOptions.RetryDelayMs
            );

            builderLogger.LogInformation(
                "Registering {HandlerCount} handlers for polling router (module: {ModuleKey})",
                pollingHandlers.Count,
                _moduleKey);

            // Register only polling handlers
            using var scope = sp.CreateScope();
            foreach (var handlerReg in pollingHandlers)
            {
                var handler =
                    (IEventHandler)scope.ServiceProvider.GetRequiredKeyedService(handlerReg.HandlerType, _moduleKey);
                var subscriptionId = GetSubscriptionId(handler);
                var supportedEventTypes = GetSupportedEventTypes(handler);
                var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
                var handlerLogger = loggerFactory.CreateLogger(handlerReg.HandlerType);

                builderLogger.LogInformation(
                    "Registering polling handler: {HandlerType} (subscription: {SubscriptionId}, mode: {Mode}, events: {EventTypes})",
                    handlerReg.HandlerType.Name,
                    subscriptionId,
                    handlerReg.Mode,
                    string.Join(", ", supportedEventTypes));

                router.RegisterHandler(new HandlerRegistration
                {
                    SubscriptionId = subscriptionId, HandlerType = handlerReg.HandlerType,
                    SupportedEventTypes = supportedEventTypes, Logger = handlerLogger
                });
            }

            return router;
        });

        // Register SubscriptionPollingService as hosted service
        _services.AddSingleton<IHostedService>(sp => new SubscriptionPollingService(
            _moduleKey,
            sp.GetRequiredKeyedService<EventRouter>(pollingRouterKey),
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

    private static IReadOnlySet<string>? GetEventTypesFromRouter(EventRouter router)
    {
        // Use the public method to get handlers instead of reflection
        var handlers = router.GetHandlers();

        if (handlers.Count == 0)
            return null;

        var allEventTypes = new HashSet<string>();
        foreach (var handler in handlers)
        {
            foreach (var eventType in handler.SupportedEventTypes)
            {
                allEventTypes.Add(eventType);
            }
        }

        return allEventTypes.Count > 0 ? allEventTypes : null;
    }
}

/// <summary>
/// Internal hosted service to manage channel registration lifecycle
/// </summary>
internal sealed class ChannelRegistrationService(
    ChannelSubscriptionRegistry registry,
    ChannelWriter<GlobalEventEnvelope> writer,
    string moduleKey,
    IReadOnlySet<string>? eventTypeFilter,
    ILogger<ChannelRegistrationService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Register with the subscription registry
        logger.LogInformation(
            "Registering channel subscription for module '{ModuleKey}' with event filter: {EventTypes}",
            moduleKey,
            eventTypeFilter == null ? "ALL EVENTS" : string.Join(", ", eventTypeFilter));

        registry.Register(moduleKey, writer, eventTypeFilter);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Unregister from the registry
        logger.LogInformation("Unregistering channel subscription for module '{ModuleKey}'", moduleKey);
        registry.Unregister(moduleKey);

        // Try to complete the channel (may already be completed by ChannelSubscriptionService)
        try
        {
            writer.Complete();
        }
        catch (ChannelClosedException)
        {
            // Channel already closed, ignore
        }

        return Task.CompletedTask;
    }
}