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
    public static DecisionResult Ship(
        IShipOrderState state,
        string trackingNumber,
        string carrier,
        DateTimeOffset shippedAt)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeShipped)
            return DecisionResult.Fail($"Order cannot be shipped in {state.Status} status");

        if (string.IsNullOrWhiteSpace(trackingNumber))
            return DecisionResult.Fail("Tracking number is required");

        if (string.IsNullOrWhiteSpace(carrier))
            return DecisionResult.Fail("Carrier is required");

        return DecisionResult.Ok(new OrderShipped(state.OrderId, trackingNumber, carrier, shippedAt));
    }

    public OrderState Apply(OrderState state, OrderShipped e) => state with
    {
        Status = OrderStatus.Shipped,
        TrackingNumber = e.TrackingNumber,
        Carrier = e.Carrier,
        ShippedAt = e.ShippedAt
    };
}
