using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

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
    public static Decision Deliver(IDeliverOrderState state, DateTimeOffset deliveredAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeDelivered)
            return Decision.Fail(OrderProblems.InvalidStatus("delivered", state.Status));

        return Decision.Succeed(new OrderDelivered(state.OrderId, deliveredAt));
    }

    public OrderState Apply(OrderState state, OrderDelivered e) => state with
    {
        Status = OrderStatus.Delivered,
        DeliveredAt = e.DeliveredAt
    };
}
