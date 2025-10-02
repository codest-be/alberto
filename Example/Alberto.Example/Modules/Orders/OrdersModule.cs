using Alberto.EventStore;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres;
using Microsoft.AspNetCore.Mvc;

namespace Alberto.Example.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
        => services
            .AddPostgresEventStore<OrderEventStoreFactory>(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                     throw new InvalidOperationException("Connection string 'alberto-db' not found.");
                o.Schema = "orders";
            });

    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("orders");

        orders.MapGet("/{id:guid}",
            async Task<IResult> (Guid id, OrderEventStoreFactory eventStoreFactory) =>
            {
                var events = await eventStoreFactory.Stream(new StreamQuery(tags:
                    [new EventTag("order", id.ToString())]));
                var order = Order.Create(events.ToArray());
                return Results.Ok(order);
            });

        return endpoints;
    }
}

public class Order
{
    public static Order Create(IEventEnvelope[] events)
    {
        // Implement event sourcing logic to reconstruct the Order from events
        return new Order();
    }
}