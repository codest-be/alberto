namespace Alberto.Orders.Core.Order.Actions;

public interface IAddItemState
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
    /// Adds an item to the order.
    /// </summary>
    public static DecisionResult AddItem(
        IAddItemState state,
        Guid productId,
        string productName,
        int quantity,
        decimal unitPrice)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeModified)
            return DecisionResult.Fail($"Order cannot be modified in {state.Status} status");

        if (quantity <= 0)
            return DecisionResult.Fail("Quantity must be greater than zero");

        if (unitPrice < 0)
            return DecisionResult.Fail("Unit price cannot be negative");

        return DecisionResult.Ok(new OrderItemAdded(state.OrderId, productId, productName, quantity, unitPrice));
    }

    public OrderState Apply(OrderState state, OrderItemAdded e) => state with
    {
        LineItems = state.LineItems
            .Where(x => x.ProductId != e.ProductId)
            .Append(new OrderLineItem(e.ProductId, e.ProductName, e.Quantity, e.UnitPrice))
            .ToList()
    };
}
