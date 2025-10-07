using Alberto.EventSourcing.Projectors;
using Alberto.Example.Modules.Orders.EventHandlers;

// ReSharper disable All

namespace Alberto.Example.Modules.Orders;

/// <summary>
/// Projector that reconstructs OrderState from events
/// </summary>
public sealed class OrderProjector : IProjector<OrderState>
{
    public OrderState Apply(OrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated e => state with
            {
                OrderId = e.OrderId, Amount = e.Amount, CustomerId = e.CustomerId, Status = OrderStatus.Created
            },
            OrderPlaced e => state with { Status = OrderStatus.Placed },
            OrderShipped e => state with { Status = OrderStatus.Shipped, TrackingNumber = e.TrackingNumber },
            OrderCancelled e => state with { Status = OrderStatus.Cancelled, CancellationReason = e.Reason },
            _ => state
        };
    }
}