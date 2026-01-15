namespace Alberto.Orders.Core.Order.Actions;

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
    public static DecisionResult RemoveItem(IRemoveItemState state, Guid productId)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeModified)
            return DecisionResult.Fail($"Order cannot be modified in {state.Status} status");

        if (state.LineItems.All(x => x.ProductId != productId))
            return DecisionResult.Fail($"Product {productId} not found in order");

        return DecisionResult.Ok(new OrderItemRemoved(state.OrderId, productId));
    }

    public OrderState Apply(OrderState state, OrderItemRemoved e) => state with
    {
        LineItems = state.LineItems.Where(x => x.ProductId != e.ProductId).ToList()
    };
}
