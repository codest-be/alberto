using Alberto.ComponentTests;
using Alberto.CQRS;
using Alberto.EventSourcing.Projections;
using Alberto.EventStore;
using Alberto.EventStore.InMemory;
using Alberto.EventStore.Subscriptions.Channel;
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
                        .Configure(options =>
                        {
                            options.RetryDelayMs = 10;
                        })
                        .WithFilter<SubscriptionEventCollectorFilter>()
                        .AddProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>(
                            mode: SubscriptionMode.Hybrid) // Test hybrid mode
                        .AddProjection<OrderEventStore, OrderStatisticsSubscription, string, OrderStatistics,
                            OrderStatisticsProjector>(
                            mode: SubscriptionMode.Async) // Test async mode
                )
                .WithCQRS(cqrs => cqrs.ScanAssembly(typeof(OrdersModule).Assembly))
                .WithTelemetry()
            );
    }
}