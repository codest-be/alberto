using Alberto.Dcb;

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
    public static Decision Cancel(ICancelOrderState state, string reason, DateTimeOffset cancelledAt)
    {
        if (!state.Exists)
            return Problem.Create("order-not-found", "Order does not exist");

        if (!state.CanBeCancelled)
            return Problem.Create("order-not-cancellable", $"Order cannot be cancelled in {state.Status} status");

        if (string.IsNullOrWhiteSpace(reason))
            return Problem.Create("cancellation-reason-required", "Cancellation reason is required");

        return Decision.Succeed(new OrderCancelled(state.OrderId, reason, cancelledAt));
    }

    public OrderState Apply(OrderState state, OrderCancelled e) => state with
    {
        Status = OrderStatus.Cancelled,
        CancellationReason = e.Reason,
        CancelledAt = e.CancelledAt
    };
}
