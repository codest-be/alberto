using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Orders.Core;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure;
using OrderActions = Alberto.Orders.Core.Order.Actions.OrderDecider;
using OrderBoundary = Alberto.Orders.Core.Order.OrderDecider;

namespace Alberto.Orders.Api.GraphQL.Mutations;

/// <summary>
/// GraphQL mutations for order operations.
/// </summary>
/// <remarks>
/// Every mutation is one pipeline: <c>Handle</c> the input, <c>Load</c> the order's DCB
/// boundary (which folds the state and captures the position in one step), <c>Decide</c>
/// with a pure action, then <c>Commit</c> — appending under the same boundary the decision
/// was based on. Conflicts are retried by re-reading and re-deciding.
/// </remarks>
public static class OrderMutations
{
    private static readonly OrderEvolver _evolver = new();

    /// <summary>How many times a conflicted append is re-read and re-decided before giving up.</summary>
    private const int ConflictRetries = 3;

    /// <summary>
    /// Creates a new order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Creates a new order with the specified line items.")]
    public static async Task<CreateOrderResult> CreateOrder(
        CreateOrderInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var orderId = Guid.CreateVersion7();
        var lineItems = input.LineItems
            .Select(x => new OrderLineItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice))
            .ToList();

        // The boundary is empty for a fresh id, so the append is still conflict-checked:
        // it fails if anything claimed this order between the read and the write.
        var result = await Store(sp)
            .Handle(input)
            .Load(OrderBoundary.BoundaryFor(orderId), _evolver)
            .Decide((cmd, state) => OrderActions.Create(state, orderId, cmd.CustomerId, lineItems, cmd.Notes))
            .Commit(ct);

        result.EnsureCommitted();
        return new CreateOrderResult(orderId);
    }

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
        var result = await Store(sp)
            .Handle(input)
            .Load(cmd => OrderBoundary.BoundaryFor(cmd.OrderId), _evolver)
            .Decide((cmd, state) =>
                OrderActions.AddItem(state, cmd.ProductId, cmd.ProductName, cmd.Quantity, cmd.UnitPrice))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Removes an item from an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Removes a line item from a draft order.")]
    public static async Task<MutationResult> RemoveOrderItem(
        Guid orderId,
        Guid productId,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(productId)
            .Load(OrderBoundary.BoundaryFor(orderId), _evolver)
            .Decide((product, state) => OrderActions.RemoveItem(state, product))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Confirms an order for processing.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Confirms a draft order, making it ready for shipment.")]
    public static async Task<MutationResult> ConfirmOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(orderId)
            .Load(OrderBoundary.BoundaryFor(orderId), _evolver)
            .Decide(state => OrderActions.Confirm(state, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Ships an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Ships a confirmed order with tracking information.")]
    public static async Task<MutationResult> ShipOrder(
        ShipOrderInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(input)
            .Load(cmd => OrderBoundary.BoundaryFor(cmd.OrderId), _evolver)
            .Decide((cmd, state) =>
                OrderActions.Ship(state, cmd.TrackingNumber, cmd.Carrier, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Marks an order as delivered.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Marks a shipped order as delivered.")]
    public static async Task<MutationResult> DeliverOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(orderId)
            .Load(OrderBoundary.BoundaryFor(orderId), _evolver)
            .Decide(state => OrderActions.Deliver(state, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Cancels an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Cancels a draft or confirmed order.")]
    public static async Task<MutationResult> CancelOrder(
        CancelOrderInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(input)
            .Load(cmd => OrderBoundary.BoundaryFor(cmd.OrderId), _evolver)
            .Decide((cmd, state) => OrderActions.Cancel(state, cmd.Reason, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    private static AlbertoStore Store(IServiceProvider sp) =>
        sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey);
}
