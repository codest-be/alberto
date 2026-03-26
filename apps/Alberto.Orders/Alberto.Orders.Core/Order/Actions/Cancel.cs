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
    public static DecisionResult<IEvent> Cancel(ICancelOrderState state, string reason, DateTimeOffset cancelledAt)
    {
        if (!state.Exists)
            return DecisionResult<IEvent>.Failure("Order does not exist");

        if (!state.CanBeCancelled)
            return DecisionResult<IEvent>.Failure($"Order cannot be cancelled in {state.Status} status");

        if (string.IsNullOrWhiteSpace(reason))
            return DecisionResult<IEvent>.Failure("Cancellation reason is required");

        return DecisionResult<IEvent>.Success(new OrderCancelled(state.OrderId, reason, cancelledAt));
    }

    public OrderState Apply(OrderState state, OrderCancelled e) => state with
    {
        Status = OrderStatus.Cancelled,
        CancellationReason = e.Reason,
        CancelledAt = e.CancelledAt
    };
}
