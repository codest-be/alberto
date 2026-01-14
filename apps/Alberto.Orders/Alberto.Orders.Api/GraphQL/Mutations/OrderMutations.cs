using System.Text.Json;
using Alberto.Dcb;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Orders.Core;
using Alberto.Orders.Infrastructure;
using HotChocolate;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Api.GraphQL.Mutations;

/// <summary>
/// GraphQL mutations for order operations.
/// </summary>
public static class OrderMutations
{
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
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var orderId = Guid.CreateVersion7();

        var lineItems = input.LineItems
            .Select(x => new OrderLineItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice))
            .ToList();

        var state = new OrderState();
        var decision = OrderDecider.Create(state, orderId, input.CustomerId, lineItems, input.Notes);

        decision.EnsureSuccess();

        var events = new[]
        {
            new EventToPersist
            {
                TenantId = "default",
                EventType = EventType.FromType(decision.Event!.GetType()),
                Tags = [new EventTag(Tags.Order, orderId.ToString())],
                EventData = JsonSerializer.Serialize(decision.Event, decision.Event.GetType())
            }
        };

        await eventStore.AppendAsync("default", events, OrderDecider.BoundaryFor(orderId), cancellationToken: ct);

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
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, input.OrderId, ct);
        var decision = OrderDecider.AddItem(state, input.ProductId, input.ProductName, input.Quantity, input.UnitPrice);

        decision.EnsureSuccess();

        await AppendEvent(eventStore, input.OrderId, state.OrderId != Guid.Empty ? state : null, decision.Event!, ct);

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
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, orderId, ct);
        var decision = OrderDecider.RemoveItem(state, productId);

        decision.EnsureSuccess();

        await AppendEvent(eventStore, orderId, state, decision.Event!, ct);

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
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, orderId, ct);
        var decision = OrderDecider.Confirm(state, timeProvider.GetUtcNow());

        decision.EnsureSuccess();

        await AppendEvent(eventStore, orderId, state, decision.Event!, ct);

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
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, input.OrderId, ct);
        var decision = OrderDecider.Ship(state, input.TrackingNumber, input.Carrier, timeProvider.GetUtcNow());

        decision.EnsureSuccess();

        await AppendEvent(eventStore, input.OrderId, state, decision.Event!, ct);

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
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, orderId, ct);
        var decision = OrderDecider.Deliver(state, timeProvider.GetUtcNow());

        decision.EnsureSuccess();

        await AppendEvent(eventStore, orderId, state, decision.Event!, ct);

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
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(OrdersModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);

        var state = await LoadOrderState(backend, input.OrderId, ct);
        var decision = OrderDecider.Cancel(state, input.Reason, timeProvider.GetUtcNow());

        decision.EnsureSuccess();

        await AppendEvent(eventStore, input.OrderId, state, decision.Event!, ct);

        return new MutationResult();
    }

    #region Helper Methods

    private static async Task<OrderState> LoadOrderState(
        IEventStoreBackend backend,
        Guid orderId,
        CancellationToken ct)
    {
        var decider = new OrderDecider();
        var state = new OrderState();

        var events = await backend.Stream("default", OrderDecider.BoundaryFor(orderId), cancellationToken: ct);

        foreach (var envelope in events)
        {
            var eventType = envelope.EventType.Id;
            object? domainEvent = eventType switch
            {
                "order-created" => JsonSerializer.Deserialize<OrderCreated>(envelope.EventData),
                "order-item-added" => JsonSerializer.Deserialize<OrderItemAdded>(envelope.EventData),
                "order-item-removed" => JsonSerializer.Deserialize<OrderItemRemoved>(envelope.EventData),
                "order-confirmed" => JsonSerializer.Deserialize<OrderConfirmed>(envelope.EventData),
                "order-shipped" => JsonSerializer.Deserialize<OrderShipped>(envelope.EventData),
                "order-delivered" => JsonSerializer.Deserialize<OrderDelivered>(envelope.EventData),
                "order-cancelled" => JsonSerializer.Deserialize<OrderCancelled>(envelope.EventData),
                _ => null
            };

            if (domainEvent is null) continue;

            state = domainEvent switch
            {
                OrderCreated e => decider.Apply(state, e),
                OrderItemAdded e => decider.Apply(state, e),
                OrderItemRemoved e => decider.Apply(state, e),
                OrderConfirmed e => decider.Apply(state, e),
                OrderShipped e => decider.Apply(state, e),
                OrderDelivered e => decider.Apply(state, e),
                OrderCancelled e => decider.Apply(state, e),
                _ => state
            };
        }

        return state;
    }

    private static async Task AppendEvent(
        IEventStore eventStore,
        Guid orderId,
        OrderState? existingState,
        IEvent @event,
        CancellationToken ct)
    {
        var events = new[]
        {
            new EventToPersist
            {
                TenantId = "default",
                EventType = EventType.FromType(@event.GetType()),
                Tags = [new EventTag(Tags.Order, orderId.ToString())],
                EventData = JsonSerializer.Serialize(@event, @event.GetType())
            }
        };

        await eventStore.AppendAsync("default", events, OrderDecider.BoundaryFor(orderId), cancellationToken: ct);
    }

    #endregion
}
