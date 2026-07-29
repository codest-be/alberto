using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>Input for shipping an order.</summary>
public sealed record ShipOrderInput(
    Guid OrderId,
    string TrackingNumber,
    string Carrier);

/// <summary>
/// Two properties. Shipping never sees LineItems, Notes, CustomerId or the timestamps of the
/// other transitions.
/// </summary>
public sealed record ShipOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeShipped => Status == OrderStatus.Confirmed;
}

/// <remarks>
/// OrderItemAdded and OrderItemRemoved are ignored: they cannot change whether an order is
/// shippable. OrderDelivered can't either — but the refusal message names the status, so
/// leaving it out would tell a client a delivered order "cannot be shipped in Shipped status".
/// </remarks>
public sealed class ShipOrderEvolver : Evolver<ShipOrderState>,
    IEvolve<ShipOrderState, OrderCreated>,
    IEvolve<ShipOrderState, OrderConfirmed>,
    IEvolve<ShipOrderState, OrderShipped>,
    IEvolve<ShipOrderState, OrderDelivered>,
    IEvolve<ShipOrderState, OrderCancelled>
{
    public ShipOrderState Apply(ShipOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public ShipOrderState Apply(ShipOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public ShipOrderState Apply(ShipOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public ShipOrderState Apply(ShipOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public ShipOrderState Apply(ShipOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class ShipOrderDecider
{
    /// <summary>
    /// Whole-order boundary: any concurrent order event conflicts with a ship. Narrowing the
    /// type axis would let non-overlapping slices commit together, but every event that could
    /// invalidate this decision would have to be listed here or the decision silently loses
    /// updates. That argument is per slice; this one has not been made.
    /// </summary>
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        ShipOrderState state,
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

        return Decision.Succeed(
            new OrderShipped(state.OrderId, trackingNumber, carrier, shippedAt));
    }
}

public static class ShipOrderMutation
{
    /// <summary>Ships an order.</summary>
    [Mutation]
    [GraphQLDescription("Ships a confirmed order with tracking information.")]
    public static async Task<MutationResult> ShipOrder(
        ShipOrderInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(cmd => ShipOrderDecider.Boundary(cmd.OrderId), new ShipOrderEvolver())
            .Decide((cmd, state) => ShipOrderDecider.Decide(
                state, cmd.TrackingNumber, cmd.Carrier, timeProvider.GetUtcNow()))
            .RetryOnConflict(OrderSlices.ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
