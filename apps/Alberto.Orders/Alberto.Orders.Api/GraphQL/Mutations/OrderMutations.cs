using System.Text.Json;
using Alberto.Dcb;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Orders.Core;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Core.Order.Actions;
using Alberto.Orders.Infrastructure;
using HotChocolate;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using OrderActions = Alberto.Orders.Core.Order.Actions.OrderDecider;
using OrderBoundary = Alberto.Orders.Core.Order.OrderDecider;

namespace Alberto.Orders.Api.GraphQL.Mutations;

/// <summary>
/// GraphQL mutations for order operations.
/// </summary>
public static class OrderMutations
{
    private static readonly OrderEvolver _evolver = new();

    /// <summary>
    /// Creates a new order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Creates a new order with the specified line items.")]
    public static async Task<CreateOrderResult> CreateOrder(
        CreateOrderInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var orderId = Guid.CreateVersion7();

        var lineItems = input.LineItems
            .Select(x => new OrderLineItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice))
            .ToList();

        var state = new OrderState();
        var result = OrderActions.Create(state, orderId, input.CustomerId, lineItems, input.Notes);

        await AppendEvents(eventStore, orderId, result.EnsureSuccess(), ct);

        return new CreateOrderResult(orderId);
    }

    /// <summary>
    /// Adds an item to an existing order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Adds a line item to an existing draft order.")]
    public static async Task<MutationResult> AddOrderItem(
        AddOrderItemInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, input.OrderId, ct);
        var result = OrderActions.AddItem(state, input.ProductId, input.ProductName, input.Quantity, input.UnitPrice);

        await AppendEvents(eventStore, input.OrderId, result.EnsureSuccess(), ct);

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
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, orderId, ct);
        var result = OrderActions.RemoveItem(state, productId);

        await AppendEvents(eventStore, orderId, result.EnsureSuccess(), ct);

        return new MutationResult();
    }

    /// <summary>
    /// Confirms an order for processing.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Confirms a draft order, making it ready for shipment.")]
    public static async Task<MutationResult> ConfirmOrder(
        Guid orderId,
        IResolverContext context,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, orderId, ct);
        var result = OrderActions.Confirm(state, timeProvider.GetUtcNow());

        await AppendEvents(eventStore, orderId, result.EnsureSuccess(), ct);

        return new MutationResult();
    }

    /// <summary>
    /// Ships an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Ships a confirmed order with tracking information.")]
    public static async Task<MutationResult> ShipOrder(
        ShipOrderInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, input.OrderId, ct);
        var result = OrderActions.Ship(state, input.TrackingNumber, input.Carrier, timeProvider.GetUtcNow());

        await AppendEvents(eventStore, input.OrderId, result.EnsureSuccess(), ct);

        return new MutationResult();
    }

    /// <summary>
    /// Marks an order as delivered.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Marks a shipped order as delivered.")]
    public static async Task<MutationResult> DeliverOrder(
        Guid orderId,
        IResolverContext context,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, orderId, ct);
        var result = OrderActions.Deliver(state, timeProvider.GetUtcNow());

        await AppendEvents(eventStore, orderId, result.EnsureSuccess(), ct);

        return new MutationResult();
    }

    /// <summary>
    /// Cancels an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Cancels a draft or confirmed order.")]
    public static async Task<MutationResult> CancelOrder(
        CancelOrderInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, input.OrderId, ct);
        var result = OrderActions.Cancel(state, input.Reason, timeProvider.GetUtcNow());

        await AppendEvents(eventStore, input.OrderId, result.EnsureSuccess(), ct);

        return new MutationResult();
    }

    #region Helper Methods

    private static async Task<OrderState> LoadOrderState(
        IEventStoreBackend backend,
        Guid orderId,
        CancellationToken ct)
    {
        var events = await backend.Stream(OrderBoundary.BoundaryFor(orderId), cancellationToken: ct);
        return _evolver.Reconstitute(events);
    }

    private static async Task AppendEvents(
        IEventStore eventStore,
        Guid orderId,
        IReadOnlyList<IEvent> events,
        CancellationToken ct)
    {
        var toPersist = events.Select(@event => new EventToPersist
        {
            EventType = EventType.FromType(@event.GetType()),
            Tags = [new EventTag(Tags.Order, orderId.ToString())],
            EventData = JsonSerializer.Serialize(@event, @event.GetType())
        }).ToArray();

        await eventStore.AppendAsync(toPersist, OrderBoundary.BoundaryFor(orderId), cancellationToken: ct);
    }

    #endregion
}
