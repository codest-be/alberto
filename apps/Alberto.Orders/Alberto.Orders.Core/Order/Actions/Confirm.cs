using Alberto.Dcb;

namespace Alberto.Orders.Core.Order.Actions;

public interface IConfirmOrderState
{
    bool Exists { get; }
    bool CanBeConfirmed { get; }
    OrderStatus Status { get; }
    Guid OrderId { get; }
    IReadOnlyList<OrderLineItem> LineItems { get; }
}

public sealed partial class OrderDecider
{
    /// <summary>
    /// Confirms the order for processing.
    /// </summary>
    public static Decision Confirm(IConfirmOrderState state, DateTimeOffset confirmedAt)
    {
        if (!state.Exists)
            return Problem.Create("order-not-found", "Order does not exist");

        if (!state.CanBeConfirmed)
            return state.LineItems.Count == 0
                ? Problem.Create("order-empty", "Cannot confirm an empty order")
                : Problem.Create(
                    "order-not-confirmable",
                    $"Order cannot be confirmed in {state.Status} status");

        return Decision.Succeed(new OrderConfirmed(state.OrderId, confirmedAt));
    }

    public OrderState Apply(OrderState state, OrderConfirmed e) => state with
    {
        Status = OrderStatus.Confirmed,
        ConfirmedAt = e.ConfirmedAt
    };
}
