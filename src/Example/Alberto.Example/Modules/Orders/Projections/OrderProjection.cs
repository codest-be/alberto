using Alberto.EventSourcing.Projections;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;
using Alberto.Projections;

namespace Alberto.Example.Modules.Orders.Projections;

[Subscription("order-projection")]
public class OrderProjectionSubscription(
    ProjectionHandler<Guid, Order> handler) :
    IHandleEvent<OrderCreated>,
    IHandleEvent<OrderPlaced>,
    IHandleEvent<OrderShipped>,
    IHandleEvent<OrderCancelled>
{
    public ValueTask Handle(OrderCancelled @event, EventContext context, CancellationToken cancellationToken = default)
        => handler.Handle(@event.OrderId, @event, context, cancellationToken);

    public ValueTask Handle(OrderCreated @event, EventContext context, CancellationToken cancellationToken = default)
        => handler.Handle(@event.OrderId, @event, context, cancellationToken);

    public ValueTask Handle(OrderPlaced @event, EventContext context, CancellationToken cancellationToken = default)
        => handler.Handle(@event.OrderId, @event, context, cancellationToken);

    public ValueTask Handle(OrderShipped @event, EventContext context, CancellationToken cancellationToken = default)
        => handler.Handle(@event.OrderId, @event, context, cancellationToken);
}

public record Order
{
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
    public string? TrackingNumber { get; init; }
    public string? CancellationReason { get; init; }
}

[GenerateMigration(Schema = "orders", TableName = "order")]
public class OrderProjector : IProjector<Order>
{
    public Order Apply(Order projectionState, object @event)
    {
        return @event switch
        {
            OrderCreated e => projectionState with
            {
                OrderId = e.OrderId, Amount = e.Amount, CustomerId = e.CustomerId, Status = OrderStatus.Created
            },
            OrderPlaced e => projectionState with { Status = OrderStatus.Placed },
            OrderShipped e => projectionState with { Status = OrderStatus.Shipped, TrackingNumber = e.TrackingNumber },
            OrderCancelled e => projectionState with { Status = OrderStatus.Cancelled, CancellationReason = e.Reason },
            _ => projectionState
        };
    }
}