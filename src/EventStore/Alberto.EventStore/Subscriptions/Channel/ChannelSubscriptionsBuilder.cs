using System.Reflection;
using System.Threading.Channels;
using Alberto.EventStore.Events;
using Alberto.EventStore.Subscriptions.Checkpoints;
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

        // Create the channel for event distribution
        Channel<GlobalEventEnvelope> channel;
        if (_channelOptions.BoundedCapacity.HasValue)
        {
            channel = System.Threading.Channels.Channel.CreateBounded<GlobalEventEnvelope>(
                new BoundedChannelOptions(_channelOptions.BoundedCapacity.Value) { FullMode = BoundedChannelFullMode.Wait });
        }
        else
        {
            channel = System.Threading.Channels.Channel.CreateUnbounded<GlobalEventEnvelope>();
        }

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

            // Extract event types from channel handlers
            var eventTypes = GetEventTypesFromRouter(eventRouter);

            return new ChannelRegistrationService(registry, writer, _moduleKey, eventTypes);
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

            var telemetryFilter = sp.GetKeyedService<TelemetryConsumeFilter>(_moduleKey);
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
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(_moduleKey);
            var poisonPillStore = sp.GetRequiredKeyedService<IPoisonPillStore>(_moduleKey);
            var logger = sp.GetRequiredService<ILogger<EventRouter>>();

            var router = new EventRouter(
                channelRouterKey,
                checkpointStore,
                poisonPillStore,
                sp,
                logger,
                _channelOptions.MaxRetries,
                _channelOptions.RetryDelayMs
            );

            // Register only channel handlers
            using var scope = sp.CreateScope();
            foreach (var handlerReg in channelHandlers)
            {
                var handler = (IEventHandler)scope.ServiceProvider.GetRequiredKeyedService(handlerReg.HandlerType, _moduleKey);
                var subscriptionId = GetSubscriptionId(handler);
                var supportedEventTypes = GetSupportedEventTypes(handler);
                var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
                var handlerLogger = loggerFactory.CreateLogger(handler.GetType());

                router.RegisterHandler(new HandlerRegistration
                {
                    SubscriptionId = subscriptionId, Handler = handler, SupportedEventTypes = supportedEventTypes, Logger = handlerLogger
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
            var logger = sp.GetRequiredService<ILogger<ChannelSubscriptionService>>();

            return new ChannelSubscriptionService(
                _moduleKey,
                eventRouter,
                channelReader,
                _channelOptions,
                consumers,
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

            var telemetryFilter = sp.GetKeyedService<TelemetryConsumeFilter>(_moduleKey);
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

            var router = new EventRouter(
                pollingRouterKey,
                checkpointStore,
                poisonPillStore,
                sp,
                logger,
                _pollingOptions.MaxRetries,
                _pollingOptions.RetryDelayMs
            );

            // Register only polling handlers
            using var scope = sp.CreateScope();
            foreach (var handlerReg in pollingHandlers)
            {
                var handler = (IEventHandler)scope.ServiceProvider.GetRequiredKeyedService(handlerReg.HandlerType, _moduleKey);
                var subscriptionId = GetSubscriptionId(handler);
                var supportedEventTypes = GetSupportedEventTypes(handler);
                var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
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
            sp.GetRequiredKeyedService<EventRouter>(pollingRouterKey),
            sp.GetRequiredKeyedService<PollingOptions>(_moduleKey),
            sp,
            sp.GetRequiredService<ILogger<SubscriptionPollingService>>()
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
        // Use reflection to get the registered handlers and their event types
        var handlerRegistrationsField = typeof(EventRouter)
            .GetField("_handlerRegistrations", BindingFlags.NonPublic | BindingFlags.Instance);

        if (handlerRegistrationsField == null)
            return null;

        var registrations = handlerRegistrationsField.GetValue(router) as IEnumerable<HandlerRegistration>;
        if (registrations == null)
            return null;

        var allEventTypes = new HashSet<string>();
        foreach (var registration in registrations)
        {
            foreach (var eventType in registration.SupportedEventTypes)
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
    IReadOnlySet<string>? eventTypeFilter) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Register with the subscription registry
        registry.Register(moduleKey, writer, eventTypeFilter);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Unregister from the registry
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