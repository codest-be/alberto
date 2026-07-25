using Alberto.Dcb;

namespace Alberto.Orders.Core.Order.Actions;

public interface ICreateOrderState
{
    bool Exists { get; }
}

public sealed partial class OrderDecider
{
    /// <summary>
    /// Creates a new order.
    /// </summary>
    public static Decision Create(
        ICreateOrderState state,
        Guid orderId,
        Guid customerId,
        IReadOnlyList<OrderLineItem> lineItems,
        string? notes = null)
    {
        if (state.Exists)
            return Problem.Create("order-already-exists", $"Order {orderId} already exists");

        if (customerId == Guid.Empty)
            return Problem.Create("customer-id-required", "Customer ID is required");

        return Decision.Succeed(new OrderCreated(orderId, customerId, lineItems, notes));
    }

    public OrderState Apply(OrderState state, OrderCreated e) => state with
    {
        OrderId = e.OrderId,
        CustomerId = e.CustomerId,
        LineItems = e.LineItems,
        Notes = e.Notes,
        Status = OrderStatus.Draft
    };
}
