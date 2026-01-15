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
    public static DecisionResult Confirm(IConfirmOrderState state, DateTimeOffset confirmedAt)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeConfirmed)
            return DecisionResult.Fail(state.LineItems.Count == 0
                ? "Cannot confirm an empty order"
                : $"Order cannot be confirmed in {state.Status} status");

        return DecisionResult.Ok(new OrderConfirmed(state.OrderId, confirmedAt));
    }

    public OrderState Apply(OrderState state, OrderConfirmed e) => state with
    {
        Status = OrderStatus.Confirmed,
        ConfirmedAt = e.ConfirmedAt
    };
}
