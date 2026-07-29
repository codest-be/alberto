using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

public interface IRemoveItemState
{
    bool Exists { get; }
    bool CanBeModified { get; }
    OrderStatus Status { get; }
    Guid OrderId { get; }
    IReadOnlyList<OrderLineItem> LineItems { get; }
}

public sealed partial class OrderDecider
{
    /// <summary>
    /// Removes an item from the order.
    /// </summary>
    public static Decision RemoveItem(IRemoveItemState state, Guid productId)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeModified)
            return Decision.Fail(OrderProblems.InvalidStatus("modified", state.Status));

        if (state.LineItems.All(x => x.ProductId != productId))
            return Decision.Fail(OrderProblems.ProductNotFound(productId));

        return Decision.Succeed(new OrderItemRemoved(state.OrderId, productId));
    }

    public OrderState Apply(OrderState state, OrderItemRemoved e) => state with
    {
        LineItems = state.LineItems.Where(x => x.ProductId != e.ProductId).ToList()
    };
}
