using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>
/// Confirmation refuses an empty order differently from a non-draft one, so it needs to know
/// both the status and whether any items are on the order — as ids, since the count is all the
/// guard asks and the ids are what keep add-then-add-again from counting twice.
/// </summary>
public sealed record ConfirmOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;
    public IReadOnlyList<Guid> ProductIds { get; init; } = [];

    public bool Exists => OrderId != Guid.Empty;
    public bool IsEmpty => ProductIds.Count == 0;
    public bool CanBeConfirmed => Status == OrderStatus.Draft && !IsEmpty;
}

public sealed class ConfirmOrderEvolver : Evolver<ConfirmOrderState>,
    IEvolve<ConfirmOrderState, OrderCreated>,
    IEvolve<ConfirmOrderState, OrderItemAdded>,
    IEvolve<ConfirmOrderState, OrderItemRemoved>,
    IEvolve<ConfirmOrderState, OrderConfirmed>,
    IEvolve<ConfirmOrderState, OrderShipped>,
    IEvolve<ConfirmOrderState, OrderDelivered>,
    IEvolve<ConfirmOrderState, OrderCancelled>
{
    public ConfirmOrderState Apply(ConfirmOrderState s, OrderCreated e) => s with
    {
        OrderId = e.OrderId,
        Status = OrderStatus.Draft,
        ProductIds = e.LineItems.Select(x => x.ProductId).ToList()
    };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderItemAdded e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).Append(e.ProductId).ToList()
    };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderItemRemoved e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).ToList()
    };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class ConfirmOrderDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(ConfirmOrderState state, DateTimeOffset confirmedAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeConfirmed)
            return Decision.Fail(state.IsEmpty
                ? OrderProblems.Empty()
                : OrderProblems.InvalidStatus("confirmed", state.Status));

        return Decision.Succeed(new OrderConfirmed(state.OrderId, confirmedAt));
    }
}

public static class ConfirmOrderMutation
{
    /// <summary>Confirms an order for processing.</summary>
    [Mutation]
    [GraphQLDescription("Confirms a draft order, making it ready for shipment.")]
    public static async Task<MutationResult> ConfirmOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(orderId)
            .Load(ConfirmOrderDecider.Boundary(orderId), new ConfirmOrderEvolver())
            .Decide(state => ConfirmOrderDecider.Decide(state, timeProvider.GetUtcNow()))
            .RetryOnConflict(OrderSlices.ConflictRetries)
            .Commit(ct)
            .OrThrow();

        return new MutationResult();
    }
}
