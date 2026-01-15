using System.Text.Json;
using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure;
using OrderDecider = Alberto.Orders.Core.Order.Actions.OrderDecider;
using Alberto.Orders.Infrastructure.Projections;
using Alberto.Orders.Infrastructure.ReadModels;
using HotChocolate;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Orders.Api.GraphQL.Queries;

/// <summary>
/// GraphQL queries for orders.
/// </summary>
public static class OrderQueries
{
    /// <summary>
    /// Gets an order by ID from the event store (real-time, consistent).
    /// </summary>
    [Query]
    [GraphQLDescription("Gets an order by ID, rebuilt from events for consistency.")]
    public static async Task<Order?> GetOrder(
        Guid orderId,
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);
        var state = await LoadOrderState(backend, tenantId, orderId, ct);

        if (!state.Exists)
            return null;

        return ToGraphQL(state);
    }

    /// <summary>
    /// Gets the orders overview statistics from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets aggregated order statistics from the async projection.")]
    public static async Task<OrdersOverview?> GetOrdersOverview(
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var stateStore = CreateStateStore<OrdersOverview>(sp, tenantId, nameof(OrdersOverviewProjection));

        var states = await stateStore.LoadManyAsync(
            [OrdersOverviewProjection.DocumentId],
            ct: ct);

        return states.GetValueOrDefault(OrdersOverviewProjection.DocumentId);
    }

    /// <summary>
    /// Gets recent orders from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets recent orders from the projection, ordered by last update.")]
    public static async Task<IReadOnlyList<Order>> GetRecentOrders(
        IResolverContext context,
        [Service] IServiceProvider sp,
        int limit = 20,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var stateStore = CreateStateStore<OrderSummary>(sp, tenantId, nameof(OrderSummaryProjection));

        var summaries = await stateStore.ListRecentAsync(limit, ct);
        return summaries.Select(Order.FromSummary).ToList();
    }

    #region Helper Methods

    private static string GetTenantId(IResolverContext context) =>
        context.GetGlobalState<string>(TenantHttpRequestInterceptor.TenantIdKey)
        ?? throw new InvalidOperationException("Tenant ID not found in resolver context");

    private static PostgresStateStore<TState> CreateStateStore<TState>(
        IServiceProvider sp,
        string tenantId,
        string projectionType)
    {
        var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(OrdersModule.ModuleKey);
        return new PostgresStateStore<TState>(dataSource, tenantId, projectionType, "orders");
    }

    private static async Task<OrderState> LoadOrderState(
        IEventStoreBackend backend,
        string tenantId,
        Guid orderId,
        CancellationToken ct)
    {
        var decider = new OrderDecider();
        var state = new OrderState();

        var events = await backend.Stream(tenantId, OrderDecider.BoundaryFor(orderId), cancellationToken: ct);

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

    private static Order ToGraphQL(OrderState state) => new(
        state.OrderId,
        state.CustomerId,
        state.LineItems.Select(x => new OrderItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice, x.Total)).ToList(),
        state.Notes,
        state.Status,
        state.Total,
        state.TrackingNumber,
        state.Carrier,
        state.CancellationReason,
        DateTimeOffset.MinValue, // Would need to track this in state
        state.ConfirmedAt,
        state.ShippedAt,
        state.DeliveredAt,
        state.CancelledAt,
        null);

    #endregion
}
