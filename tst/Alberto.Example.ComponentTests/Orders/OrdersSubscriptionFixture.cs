using Alberto.ComponentTests;
using Alberto.EventStore.InMemory;
using Alberto.Example.Modules.Orders.Projections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders;

/// <summary>
/// Fixture for testing subscription behavior with InMemory backend
/// </summary>
public abstract class OrdersSubscriptionFixture : ServiceFixture
{
    private readonly ITestOutputHelper _testOutputHelper;

    protected OrdersSubscriptionFixture(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    public UseCase UseCase()
    {
        return new UseCase(new ScenarioContext(_testOutputHelper, this));
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        // Use InMemory subscriptions infrastructure
        services
            .AddEventStoreWithInMemorySubscriptions<MultiTenantContext>("orders")
            .AddPolling(options =>
            {
                // Fast polling for tests
                options.MinPollingIntervalMs = 10;
                options.MaxPollingIntervalMs = 50;
                options.MaxPageSize = 100;
                options.MaxRetries = 3;
                options.RetryDelayMs = 50;
            })
            .AddSubscription<OrderProjectionSubscription>();
    }
}