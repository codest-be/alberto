using Alberto.Commands;
using Alberto;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>Input for adding an item to an order.</summary>
public sealed record AddOrderItemInput(
    Guid OrderId,
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

/// <summary>
/// What adding an item decides on: that the order exists and is still a draft.
/// </summary>
/// <remarks>
/// The status is folded from all five status events even though the guard only asks whether it
/// is <see cref="OrderStatus.Draft"/>, because the refusal names the blocking status. A slice
/// that folded only <c>OrderConfirmed</c> would tell a client that a cancelled order "cannot be
/// modified in Confirmed status".
/// <para>
/// It does not fold the line items: adding an item neither reads nor validates them, and
/// <c>OrderItemAdded</c>/<c>OrderItemRemoved</c> cannot change whether this decision succeeds.
/// </para>
/// </remarks>
public sealed record AddOrderItemState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeModified => Status == OrderStatus.Draft;
}

public sealed class AddOrderItemEvolver : Evolver<AddOrderItemState>,
    IEvolve<AddOrderItemState, OrderCreated>,
    IEvolve<AddOrderItemState, OrderConfirmed>,
    IEvolve<AddOrderItemState, OrderShipped>,
    IEvolve<AddOrderItemState, OrderDelivered>,
    IEvolve<AddOrderItemState, OrderCancelled>
{
    public AddOrderItemState Apply(AddOrderItemState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public AddOrderItemState Apply(AddOrderItemState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public AddOrderItemState Apply(AddOrderItemState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public AddOrderItemState Apply(AddOrderItemState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public AddOrderItemState Apply(AddOrderItemState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class AddOrderItemDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        AddOrderItemState state,
        Guid productId,
        string productName,
        int quantity,
        decimal unitPrice)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeModified)
            return Decision.Fail(OrderProblems.InvalidStatus("modified", state.Status));

        if (quantity <= 0)
            return Decision.Fail(OrderProblems.InvalidQuantity());

        if (unitPrice < 0)
            return Decision.Fail(OrderProblems.InvalidUnitPrice());

        return Decision.Succeed(
            new OrderItemAdded(state.OrderId, productId, productName, quantity, unitPrice));
    }
}

public static class AddOrderItemMutation
{
    /// <summary>
    /// Adds an item to an existing order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Adds a line item to an existing draft order.")]
    public static async Task<MutationResult> AddOrderItem(
        AddOrderItemInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(cmd => AddOrderItemDecider.Boundary(cmd.OrderId), new AddOrderItemEvolver())
            .Decide((cmd, state) =>
                AddOrderItemDecider.Decide(state, cmd.ProductId, cmd.ProductName, cmd.Quantity, cmd.UnitPrice))
            .RetryOnConflict(OrderSlices.ConflictRetries)
            .Commit(ct)
            .OrThrow();

        return new MutationResult();
    }
}
