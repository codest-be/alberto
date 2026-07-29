using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

public sealed partial class OrderDecider
{
    public OrderState Apply(OrderState state, OrderCreated e) => state with
    {
        OrderId = e.OrderId,
        CustomerId = e.CustomerId,
        LineItems = e.LineItems,
        Notes = e.Notes,
        Status = OrderStatus.Draft
    };
}
