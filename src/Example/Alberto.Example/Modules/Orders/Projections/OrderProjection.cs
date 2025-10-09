using Alberto.EventSourcing.Projections;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;

namespace Alberto.Example.Modules.Orders.Projections;

[Subscription("order-projection")]
public class OrderProjectionSubscription(
    ProjectionHandler<Guid, OrderState> handler) :
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

public record OrderState
{
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
    public string? TrackingNumber { get; init; }
    public string? CancellationReason { get; init; }
}

public class OrderProjector : IProjector<OrderState>
{
    public OrderState Apply(OrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated e => state with
            {
                OrderId = e.OrderId,
                Amount = e.Amount,
                CustomerId = e.CustomerId,
                Status = OrderStatus.Created
            },
            OrderPlaced e => state with
            {
                Status = OrderStatus.Placed
            },
            OrderShipped e => state with
            {
                Status = OrderStatus.Shipped,
                TrackingNumber = e.TrackingNumber
            },
            OrderCancelled e => state with
            {
                Status = OrderStatus.Cancelled,
                CancellationReason = e.Reason
            },
            _ => state
        };
    }
}