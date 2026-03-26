using Alberto.Dcb;

namespace Alberto.Orders.Core.Order.Actions;

public interface IDeliverOrderState
{
    bool Exists { get; }
    bool CanBeDelivered { get; }
    OrderStatus Status { get; }
    Guid OrderId { get; }
}

public sealed partial class OrderDecider
{
    /// <summary>
    /// Marks the order as delivered.
    /// </summary>
    public static DecisionResult<IEvent> Deliver(IDeliverOrderState state, DateTimeOffset deliveredAt)
    {
        if (!state.Exists)
            return DecisionResult<IEvent>.Failure("Order does not exist");

        if (!state.CanBeDelivered)
            return DecisionResult<IEvent>.Failure($"Order cannot be delivered in {state.Status} status");

        return DecisionResult<IEvent>.Success(new OrderDelivered(state.OrderId, deliveredAt));
    }

    public OrderState Apply(OrderState state, OrderDelivered e) => state with
    {
        Status = OrderStatus.Delivered,
        DeliveredAt = e.DeliveredAt
    };
}
