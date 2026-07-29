using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

public sealed partial class OrderDecider
{
    public OrderState Apply(OrderState state, OrderItemAdded e) => state with
    {
        LineItems = state.LineItems
            .Where(x => x.ProductId != e.ProductId)
            .Append(new OrderLineItem(e.ProductId, e.ProductName, e.Quantity, e.UnitPrice))
            .ToList()
    };
}
