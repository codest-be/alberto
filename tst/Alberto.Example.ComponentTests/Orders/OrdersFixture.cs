using Alberto.ComponentTests;
using Alberto.CQRS;
using Alberto.EventStore;
using Alberto.EventStore.InMemory;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Orders.Projections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders;

public abstract class OrdersFixture : ServiceFixture<Program>
{
    protected OrdersFixture(ITestOutputHelper testOutputHelper)
    {
        TestOutputHelper = testOutputHelper;
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services
            .AddModule<OrderEventStore>("orders", module => module
                .WithInMemory(EventStoreBackend)
                .WithMultiTenancy<MultiTenantContext>()
                .WithChannelSubscriptions(channel => channel
                    .ConfigureSync(options =>
                    {
                        options.MaxRetries = 0;
                        options.AllowParallelExecution = true;
                    })
                    .ConfigureAsync(options =>
                    {
                        options.MinPollingIntervalMs = 10;
                        options.PollingGrowFactor = 1;
                        options.MaxRetries = 0;
                    })
                    .WithFilter<SubscriptionEventCollectorFilter>()
                    .AddTestingProjection<OrderProjectionSubscription, OrderProjector, OrderEventStore>(mode: SubscriptionMode.Hybrid,
                        SubscriptionMetadataRegistry)
                    .AddTestingProjection<OrderStatisticsSubscription, OrderStatisticsProjector, OrderEventStore>(
                        mode: SubscriptionMode.Async, SubscriptionMetadataRegistry)
                )
                .WithCQRS(cqrs => cqrs.ScanAssembly(typeof(OrdersModule).Assembly))
            );
    }
}