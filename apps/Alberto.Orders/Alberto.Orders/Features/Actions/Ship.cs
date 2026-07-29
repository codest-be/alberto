using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

public sealed partial class OrderDecider
{
    public OrderState Apply(OrderState state, OrderShipped e) => state with
    {
        Status = OrderStatus.Shipped,
        TrackingNumber = e.TrackingNumber,
        Carrier = e.Carrier,
        ShippedAt = e.ShippedAt
    };
}
