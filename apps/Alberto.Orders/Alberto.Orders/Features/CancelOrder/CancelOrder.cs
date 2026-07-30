using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>Input for cancelling an order.</summary>
public sealed record CancelOrderInput(
    Guid OrderId,
    string Reason);

/// <summary>
/// Cancellation is allowed from two statuses and refused from three, so status is all it folds.
/// It does not carry the reason it is about to record — nothing about the decision depends on a
/// previous cancellation's reason.
/// </summary>
public sealed record CancelOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeCancelled => Status is OrderStatus.Draft or OrderStatus.Confirmed;
}

public sealed class CancelOrderEvolver : Evolver<CancelOrderState>,
    IEvolve<CancelOrderState, OrderCreated>,
    IEvolve<CancelOrderState, OrderConfirmed>,
    IEvolve<CancelOrderState, OrderShipped>,
    IEvolve<CancelOrderState, OrderDelivered>,
    IEvolve<CancelOrderState, OrderCancelled>
{
    public CancelOrderState Apply(CancelOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public CancelOrderState Apply(CancelOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public CancelOrderState Apply(CancelOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public CancelOrderState Apply(CancelOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public CancelOrderState Apply(CancelOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class CancelOrderDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        CancelOrderState state,
        string reason,
        DateTimeOffset cancelledAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeCancelled)
            return Decision.Fail(OrderProblems.InvalidStatus("cancelled", state.Status));

        if (string.IsNullOrWhiteSpace(reason))
            return Decision.Fail(OrderProblems.CancellationReasonRequired());

        return Decision.Succeed(new OrderCancelled(state.OrderId, reason, cancelledAt));
    }
}

public static class CancelOrderMutation
{
    /// <summary>Cancels an order.</summary>
    [Mutation]
    [GraphQLDescription("Cancels a draft or confirmed order.")]
    public static async Task<MutationResult> CancelOrder(
        CancelOrderInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(cmd => CancelOrderDecider.Boundary(cmd.OrderId), new CancelOrderEvolver())
            .Decide((cmd, state) =>
                CancelOrderDecider.Decide(state, cmd.Reason, timeProvider.GetUtcNow()))
            .RetryOnConflict(OrderSlices.ConflictRetries)
            .Commit(ct)
            .OrThrow();

        return new MutationResult();
    }
}
