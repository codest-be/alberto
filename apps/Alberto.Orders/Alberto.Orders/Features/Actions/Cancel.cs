using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

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
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeCancelled)
            return Decision.Fail(OrderProblems.InvalidStatus("cancelled", state.Status));

        if (string.IsNullOrWhiteSpace(reason))
            return Decision.Fail(OrderProblems.CancellationReasonRequired());

        return Decision.Succeed(new OrderCancelled(state.OrderId, reason, cancelledAt));
    }

    public OrderState Apply(OrderState state, OrderCancelled e) => state with
    {
        Status = OrderStatus.Cancelled,
        CancellationReason = e.Reason,
        CancelledAt = e.CancelledAt
    };
}
