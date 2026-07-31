using Alberto.Commands;
using Alberto;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>
/// Removal needs to know whether the product is on the order, which is a set of ids — not the
/// line items. Names, quantities and prices are in the same events and are not folded.
/// </summary>
public sealed record RemoveOrderItemState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;
    public IReadOnlyList<Guid> ProductIds { get; init; } = [];

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeModified => Status == OrderStatus.Draft;
}

public sealed class RemoveOrderItemEvolver : Evolver<RemoveOrderItemState>,
    IEvolve<RemoveOrderItemState, OrderCreated>,
    IEvolve<RemoveOrderItemState, OrderItemAdded>,
    IEvolve<RemoveOrderItemState, OrderItemRemoved>,
    IEvolve<RemoveOrderItemState, OrderConfirmed>,
    IEvolve<RemoveOrderItemState, OrderShipped>,
    IEvolve<RemoveOrderItemState, OrderDelivered>,
    IEvolve<RemoveOrderItemState, OrderCancelled>
{
    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderCreated e) => s with
    {
        OrderId = e.OrderId,
        Status = OrderStatus.Draft,
        ProductIds = e.LineItems.Select(x => x.ProductId).ToList()
    };

    // Mirrors OrderItemAdded's semantics: adding a product already on the order replaces it.
    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderItemAdded e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).Append(e.ProductId).ToList()
    };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderItemRemoved e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).ToList()
    };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class RemoveOrderItemDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(RemoveOrderItemState state, Guid productId)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeModified)
            return Decision.Fail(OrderProblems.InvalidStatus("modified", state.Status));

        if (state.ProductIds.All(id => id != productId))
            return Decision.Fail(OrderProblems.ProductNotFound(productId));

        return Decision.Succeed(new OrderItemRemoved(state.OrderId, productId));
    }
}

public static class RemoveOrderItemMutation
{
    /// <summary>Removes an item from an order.</summary>
    [Mutation]
    [GraphQLDescription("Removes a line item from a draft order.")]
    public static async Task<MutationResult> RemoveOrderItem(
        Guid orderId,
        Guid productId,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(productId)
            .Load(RemoveOrderItemDecider.Boundary(orderId), new RemoveOrderItemEvolver())
            .Decide((product, state) => RemoveOrderItemDecider.Decide(state, product))
            .RetryOnConflict(OrderSlices.ConflictRetries)
            .Commit(ct)
            .OrThrow();

        return new MutationResult();
    }
}
