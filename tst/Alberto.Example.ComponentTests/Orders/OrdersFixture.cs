using System.Reflection;
using Alberto.ComponentTests;
using Alberto.CQRS;
using Alberto.EventSourcing.Projections;
using Alberto.EventStore;
using Alberto.EventStore.InMemory;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.EventStore.Telemetry;
using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Orders.Projections;
using Alberto.Projections.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders;

public abstract class OrdersFixture : ServiceFixture
{
    private readonly InMemoryEventStoreBackend _eventStoreBackend;
    private readonly ITestOutputHelper _testOutputHelper;

    protected OrdersFixture(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _eventStoreBackend = new InMemoryEventStoreBackend(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InMemoryEventStoreBackend>());
    }

    /// <summary>
    /// Gets the subscription event collector for waiting on processed events in tests.
    /// </summary>
    public SubscriptionEventCollector SubscriptionCollector { get; } = new();

    public UseCase UseCase()
    {
        return new UseCase(new ScenarioContext(_testOutputHelper, this));
    }

    public override async ValueTask InitializeAsync()
    {
        // Initialize the service provider first
        await base.InitializeAsync();

        // Auto-discover subscription metadata from registered handlers
        var metadataRegistry = new SubscriptionMetadataRegistry();
        const string moduleKey = "orders";

        // Get all subscription handlers registered with the module key
        var handlers =
            Services.GetKeyedServices<IEventHandler>(moduleKey);

        foreach (var handler in handlers)
        {
            var handlerType = handler.GetType();

            // Extract subscription ID from [Subscription] attribute
            var subscriptionAttribute = handlerType
                .GetCustomAttribute<SubscriptionAttribute>();
            var subscriptionId = subscriptionAttribute?.SubscriptionId ?? handlerType.Name;

            // Extract event types from IHandleEvent<> interfaces
            var handleInterfaces = handlerType
                .GetInterfaces()
                .Where(i => i.IsGenericType &&
                            i.GetGenericTypeDefinition() ==
                            typeof(IHandleEvent<>));

            var eventTypes = handleInterfaces
                .Select(i => i.GetGenericArguments()[0].Name)
                .ToArray();

            metadataRegistry.RegisterSubscription(subscriptionId, eventTypes);
        }

        // Wire up the metadata registry to the collector
        SubscriptionCollector.SetMetadataRegistry(metadataRegistry);
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddInMemoryProjectionRepository<Guid, Order, OrderProjector>();
        services.AddInMemoryProjectionRepository<string, OrderStatistics, OrderStatisticsProjector>();

        services.AddSingleton(SubscriptionCollector);

        services
            .AddModule<OrderEventStore>("orders", module => module
                .WithInMemory(_eventStoreBackend)
                .WithMultiTenancy<MultiTenantContext>()
                .WithChannelSubscriptions(channel => channel
                    .ConfigureSync(options =>
                    {
                        options.MaxRetries = 0;
                        options.AllowParallelExecution = true;
                    })
                    .ConfigureAsync(options =>
                    {
                        options.MinPollingIntervalMs = 25;
                        options.PollingGrowFactor = 1;
                        options.MaxRetries = 0;
                    })
                    .WithFilter<SubscriptionEventCollectorFilter>()
                    .AddProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>(mode: SubscriptionMode.Hybrid)
                    .AddProjection<OrderEventStore, OrderStatisticsSubscription, string, OrderStatistics, OrderStatisticsProjector>(mode: SubscriptionMode.Async)
                )
                .WithCQRS(cqrs => cqrs.ScanAssembly(typeof(OrdersModule).Assembly))
                .WithTelemetry()
            );
    }
}