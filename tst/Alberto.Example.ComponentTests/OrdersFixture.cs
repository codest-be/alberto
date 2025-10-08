using Alberto.CQRS.Registration;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.InMemory;
using Alberto.EventStore.MultiTenant;
using Alberto.Example.Modules.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alberto.Example.IntegrationTests;

public abstract class OrdersFixture
{
    private readonly InMemoryEventStoreBackend _eventStoreBackend;

    protected OrdersFixture()
    {
        _eventStoreBackend = new InMemoryEventStoreBackend(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InMemoryEventStoreBackend>());
    }

    public UseCase UseCase()
    {
        var services = CreateServiceCollection();
        return new UseCase(services, _eventStoreBackend);
    }

    private IServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // Add tenant context
        services.AddSingleton<ITenantContext, TestTenantContext>();

        // Add InMemory EventStore backend
        services.AddSingleton<IEventStoreBackend>(_eventStoreBackend);

        // Add OrderEventStore
        services.AddScoped<OrderEventStore>();

        // Add CQRS (commands, handlers, validators)
        services.AddCQRS(b => b.ScanAssembly(typeof(OrdersModule).Assembly));

        // Add EventSourced repository
        services.AddEventSourcedRepository<OrderState, OrderProjector, OrderEventStore>();

        return services;
    }
}