using Alberto.ComponentTests;
using Alberto.EventSourcing.Projections;
using Alberto.EventStore;
using Alberto.EventStore.InMemory;
using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Orders.Projections;
using Alberto.Projections.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders;

/// <summary>
/// Fixture for testing subscription behavior with InMemory backend
/// </summary>
public abstract class OrdersSubscriptionFixture(ITestOutputHelper testOutputHelper) : ServiceFixture
{
    public UseCase UseCase()
    {
        return new UseCase(new ScenarioContext(testOutputHelper, this));
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        // Use InMemory backend with subscriptions
        services.AddModule<OrderEventStore>("orders", module => module
            .WithInMemory()
            .WithMultiTenancy<MultiTenantContext>()
            .WithPollingSubscriptions(polling => polling
                .Configure(options =>
                {
                    // Fast polling for tests
                    options.MinPollingIntervalMs = 10;
                    options.MaxPollingIntervalMs = 50;
                    options.MaxPageSize = 100;
                    options.MaxRetries = 3;
                    options.RetryDelayMs = 50;
                })
                .AddProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>()
            )
        );

        // Register projection repository
        services.AddInMemoryProjectionRepository<Guid, Order, OrderProjector>();
    }
}