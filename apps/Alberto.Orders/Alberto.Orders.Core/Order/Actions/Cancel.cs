namespace Alberto.Orders.Core.Order.Actions;

public interface ICancelOrderState
{
    bool Exists { get; }
    bool CanBeCancelled { get; }
    OrderStatus Status { get; }
    Guid OrderId { get; }
}

public sealed partial class OrderDecider
{
    /// <summary>
    /// Cancels the order.
    /// </summary>
    public static DecisionResult Cancel(ICancelOrderState state, string reason, DateTimeOffset cancelledAt)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeCancelled)
            return DecisionResult.Fail($"Order cannot be cancelled in {state.Status} status");

        if (string.IsNullOrWhiteSpace(reason))
            return DecisionResult.Fail("Cancellation reason is required");

        return DecisionResult.Ok(new OrderCancelled(state.OrderId, reason, cancelledAt));
    }

    public OrderState Apply(OrderState state, OrderCancelled e) => state with
    {
        Status = OrderStatus.Cancelled,
        CancellationReason = e.Reason,
        CancelledAt = e.CancelledAt
    };
}
