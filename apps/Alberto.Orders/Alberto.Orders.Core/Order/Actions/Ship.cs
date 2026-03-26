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
    public static DecisionResult<IEvent> Ship(
        IShipOrderState state,
        string trackingNumber,
        string carrier,
        DateTimeOffset shippedAt)
    {
        if (!state.Exists)
            return DecisionResult<IEvent>.Failure("Order does not exist");

        if (!state.CanBeShipped)
            return DecisionResult<IEvent>.Failure($"Order cannot be shipped in {state.Status} status");

        if (string.IsNullOrWhiteSpace(trackingNumber))
            return DecisionResult<IEvent>.Failure("Tracking number is required");

        if (string.IsNullOrWhiteSpace(carrier))
            return DecisionResult<IEvent>.Failure("Carrier is required");

        return DecisionResult<IEvent>.Success(new OrderShipped(state.OrderId, trackingNumber, carrier, shippedAt));
    }

    public OrderState Apply(OrderState state, OrderShipped e) => state with
    {
        Status = OrderStatus.Shipped,
        TrackingNumber = e.TrackingNumber,
        Carrier = e.Carrier,
        ShippedAt = e.ShippedAt
    };
}
