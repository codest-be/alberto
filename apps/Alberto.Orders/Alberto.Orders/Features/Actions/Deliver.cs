using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

public sealed partial class OrderDecider
{
    public OrderState Apply(OrderState state, OrderDelivered e) => state with
    {
        Status = OrderStatus.Delivered,
        DeliveredAt = e.DeliveredAt
    };
}
