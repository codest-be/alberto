using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.EventStore.Postgres;

namespace Alberto.Example.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        return services
            .AddPostgresEventStore<OrderEventStore>(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                     throw new InvalidOperationException("Connection string 'alberto-db' not found.");
                o.Schema = "orders";
            });
    }

    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder orders = endpoints.MapGroup("orders");

        orders.MapGet("/{id:guid}",
            async Task<IResult> (Guid id, OrderEventStore eventStore) =>
            {
                IReadOnlyCollection<IEventEnvelope> events =
                    await eventStore.Stream(new StreamQuery([new EventTag("order", id.ToString())]));
                Order order = Order.Create(events.ToArray());
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