using Alberto.ComponentTests;
using Alberto.EventStore.InMemory;
using Alberto.EventStore.MultiTenant;
using Alberto.Example.Modules.Orders;
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

    public UseCase UseCase()
    {
        return new UseCase(new ScenarioContext(_testOutputHelper, this));
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddScoped<OrderEventStore>(sp =>
            new OrderEventStore(sp.GetRequiredService<ITenantContext>(), _eventStoreBackend));
    }
}