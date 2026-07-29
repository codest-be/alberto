using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

public sealed partial class OrderDecider
{
    public OrderState Apply(OrderState state, OrderConfirmed e) => state with
    {
        Status = OrderStatus.Confirmed,
        ConfirmedAt = e.ConfirmedAt
    };
}
