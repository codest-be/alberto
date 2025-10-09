using Alberto.EventStore.InMemory;
using Alberto.EventStore.MultiTenant;
using Alberto.Example.Modules.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alberto.Example.ComponentTests;

public abstract class OrdersFixture : ServiceFixture
{
    private readonly InMemoryEventStoreBackend _eventStoreBackend;

    protected OrdersFixture()
    {
        _eventStoreBackend = new InMemoryEventStoreBackend(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InMemoryEventStoreBackend>());
    }

    public UseCase UseCase()
    {
        return new UseCase(_eventStoreBackend, HttpClient, new TestTenantContext().Tenant);
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddScoped<OrderEventStore>(sp =>
            new OrderEventStore(sp.GetRequiredService<ITenantContext>(), _eventStoreBackend));
    }
}