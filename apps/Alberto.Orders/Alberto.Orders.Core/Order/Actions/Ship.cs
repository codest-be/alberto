using Alberto.Dcb;

namespace Alberto.Orders.Core.Order.Actions;

public interface IShipOrderState
{
    bool Exists { get; }
    bool CanBeShipped { get; }
    OrderStatus Status { get; }
    Guid OrderId { get; }
}

public sealed partial class OrderDecider
{
    /// <summary>
    /// Ships the order.
    /// </summary>
    public static Decision Ship(
        IShipOrderState state,
        string trackingNumber,
        string carrier,
        DateTimeOffset shippedAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeShipped)
            return Decision.Fail(OrderProblems.InvalidStatus("shipped", state.Status));

        if (string.IsNullOrWhiteSpace(trackingNumber))
            return Decision.Fail(OrderProblems.TrackingNumberRequired());

        if (string.IsNullOrWhiteSpace(carrier))
            return Decision.Fail(OrderProblems.CarrierRequired());

        return Decision.Succeed(new OrderShipped(state.OrderId, trackingNumber, carrier, shippedAt));
    }

    public OrderState Apply(OrderState state, OrderShipped e) => state with
    {
        Status = OrderStatus.Shipped,
        TrackingNumber = e.TrackingNumber,
        Carrier = e.Carrier,
        ShippedAt = e.ShippedAt
    };
}
