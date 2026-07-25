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
            return Problem.Create("order-not-found", "Order does not exist");

        if (!state.CanBeShipped)
            return Problem.Create("order-not-shippable", $"Order cannot be shipped in {state.Status} status");

        if (string.IsNullOrWhiteSpace(trackingNumber))
            return Problem.Create("tracking-number-required", "Tracking number is required");

        if (string.IsNullOrWhiteSpace(carrier))
            return Problem.Create("carrier-required", "Carrier is required");

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
