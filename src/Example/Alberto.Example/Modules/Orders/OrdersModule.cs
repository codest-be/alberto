using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Subscriptions;
using Alberto.EventStore.Telemetry;
using Alberto.Example;
using Alberto.Example.Modules.Orders.EventHandlers;
using Alberto.Example.Modules.Orders.Filters;
using System.Text.Json;

namespace Alberto.Example.Modules.Orders;

// Request model for creating orders
public record CreateOrderRequest(decimal Amount, string CustomerId);

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddEventStore<OrderEventStore, MultiTenantContext>("orders", options =>
            {
                options.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                           throw new InvalidOperationException("Connection string 'alberto-db' not found.");
                options.Schema = "orders";
            })
            .AddEventPolling("orders-polling", options =>
            {
                options.MinPollingIntervalMs = 100;
                options.MaxPollingIntervalMs = 5000;
                options.MaxPageSize = 100;
                options.MaxRetries = 3;
                options.RetryDelayMs = 1000;
            })
            .Pipeline(pipeline => pipeline.AddConsumeFilter<LoggingFilter>())
            .AddEventHandler<OrderEventHandler>()
            .AddEventHandler<OrderAnalyticsHandler>();

        return services;
    }

    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder orders = endpoints.MapGroup("orders");

        orders.MapPost("/",
            async Task<IResult> (CreateOrderRequest request, OrderEventStore eventStore, ILogger<OrderEventStore> logger) =>
            {
                try
                {
                    var orderId = Guid.NewGuid().ToString();
                    var orderCreated = new OrderCreated(
                        orderId,
                        request.Amount,
                        request.CustomerId,
                        DateTimeOffset.UtcNow
                    );

                    var eventToPersist = new EventToPersist
                    {
                        EventType = EventType.GetEventType(typeof(OrderCreated))!,
                        EventJson = JsonSerializer.Serialize(orderCreated),
                        Tags = [new EventTag("order", orderId)],
                        Metadata = new Dictionary<string, string>
                        {
                            ["customer"] = request.CustomerId
                        },
                        Created = DateTimeOffset.UtcNow
                    };

                    var persistedEvents = await eventStore.Append([eventToPersist], null, null);
                    logger.LogInformation("Order created with ID {OrderId}", orderId);
                    var persistedEvent = persistedEvents.First();

                    return Results.Created($"/orders/{orderId}", new { orderId, EventId = persistedEvent.Id });
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Failed to create order: {ex.Message}");
                }
            });

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