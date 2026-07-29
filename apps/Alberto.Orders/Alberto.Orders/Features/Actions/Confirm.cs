using Alberto.Orders.Contracts;
using Alberto.Dcb;

namespace Alberto.Orders.Features;

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
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeConfirmed)
            return Decision.Fail(state.LineItems.Count == 0
                ? OrderProblems.Empty()
                : OrderProblems.InvalidStatus("confirmed", state.Status));

        return Decision.Succeed(new OrderConfirmed(state.OrderId, confirmedAt));
    }

    public OrderState Apply(OrderState state, OrderConfirmed e) => state with
    {
        Status = OrderStatus.Confirmed,
        ConfirmedAt = e.ConfirmedAt
    };
}
