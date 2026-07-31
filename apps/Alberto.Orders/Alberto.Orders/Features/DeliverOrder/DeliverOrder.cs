using Alberto.Commands;
using Alberto;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>
/// Delivery reads the same two properties as shipping, and folds them from its own evolver. The
/// shape being identical to ShipOrderState is not a reason to share one: they are free to
/// diverge, and a shared record is what made every action carry every field.
/// </summary>
public sealed record DeliverOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeDelivered => Status == OrderStatus.Shipped;
}

public sealed class DeliverOrderEvolver : Evolver<DeliverOrderState>,
    IEvolve<DeliverOrderState, OrderCreated>,
    IEvolve<DeliverOrderState, OrderConfirmed>,
    IEvolve<DeliverOrderState, OrderShipped>,
    IEvolve<DeliverOrderState, OrderDelivered>,
    IEvolve<DeliverOrderState, OrderCancelled>
{
    public DeliverOrderState Apply(DeliverOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public DeliverOrderState Apply(DeliverOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public DeliverOrderState Apply(DeliverOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public DeliverOrderState Apply(DeliverOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public DeliverOrderState Apply(DeliverOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class DeliverOrderDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(DeliverOrderState state, DateTimeOffset deliveredAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeDelivered)
            return Decision.Fail(OrderProblems.InvalidStatus("delivered", state.Status));

        return Decision.Succeed(new OrderDelivered(state.OrderId, deliveredAt));
    }
}

public static class DeliverOrderMutation
{
    /// <summary>Marks an order as delivered.</summary>
    [Mutation]
    [GraphQLDescription("Marks a shipped order as delivered.")]
    public static async Task<MutationResult> DeliverOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(orderId)
            .Load(DeliverOrderDecider.Boundary(orderId), new DeliverOrderEvolver())
            .Decide(state => DeliverOrderDecider.Decide(state, timeProvider.GetUtcNow()))
            .RetryOnConflict(OrderSlices.ConflictRetries)
            .Commit(ct)
            .OrThrow();

        return new MutationResult();
    }
}
