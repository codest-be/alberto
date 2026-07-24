using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure;
using Alberto.Orders.Infrastructure.Data;
using OrderBoundary = Alberto.Orders.Core.Order.OrderDecider;
using Alberto.Orders.Infrastructure.Projections;
using Alberto.Orders.Infrastructure.ReadModels;
using HotChocolate.Resolvers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Alberto.Orders.Api.GraphQL.Queries;

/// <summary>
/// GraphQL queries for orders.
/// </summary>
public static class OrderQueries
{
    private static readonly OrderEvolver _evolver = new();

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
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);
        var state = await LoadOrderState(backend, orderId, ct);

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
        var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(OrdersModule.ModuleKey);
        var stateStore = new PostgresStateStore<OrdersOverview>(
            dataSource,
            projectionType: nameof(OrdersOverviewProjection),
            schema: "orders",
            tenantId: tenantId);

        var states = await stateStore.LoadManyAsync(
            [OrdersOverviewProjection.DocumentId],
            ct: ct);

        return states.GetValueOrDefault(OrdersOverviewProjection.DocumentId);
    }

    /// <summary>
    /// Gets orders with optional filtering, sorting, and pagination.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets orders with optional filtering by status, customer, and date range.")]
    public static async Task<OrdersConnection> GetOrders(
        IResolverContext context,
        [Service] IDbContextFactory<OrdersDbContext> contextFactory,
        OrderStatus? status = null,
        Guid? customerId = null,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var query = dbContext.OrderSummaries
            .Where(o => o.TenantId == tenantId);

        // Apply filters
        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);

        if (customerId.HasValue)
            query = query.Where(o => o.CustomerId == customerId.Value);

        if (createdAfter.HasValue)
            query = query.Where(o => o.CreatedAt >= createdAfter.Value);

        if (createdBefore.HasValue)
            query = query.Where(o => o.CreatedAt <= createdBefore.Value);

        // Get total count for pagination
        var totalCount = await query.CountAsync(ct);

        // Get page of results
        var entities = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new OrdersConnection(
            entities.Select(Order.FromEntity).ToList(),
            totalCount,
            skip,
            take);
    }

    /// <summary>
    /// Gets recent orders (convenience method).
    /// </summary>
    [Query]
    [GraphQLDescription("Gets recent orders, ordered by creation date.")]
    public static async Task<IReadOnlyList<Order>> GetRecentOrders(
        IResolverContext context,
        [Service] IDbContextFactory<OrdersDbContext> contextFactory,
        int limit = 20,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var entities = await dbContext.OrderSummaries
            .Where(o => o.TenantId == tenantId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(Order.FromEntity).ToList();
    }

    #region Helper Methods

    private static string GetTenantId(IResolverContext context) =>
        context.GetGlobalState<string>(TenantHttpRequestInterceptor.TenantIdKey)
        ?? throw new InvalidOperationException("Tenant ID not found in resolver context");

    private static async Task<OrderState> LoadOrderState(
        IEventStoreBackend backend,
        Guid orderId,
        CancellationToken ct)
    {
        var events = await backend.StreamAsync(OrderBoundary.BoundaryFor(orderId), cancellationToken: ct);
        return _evolver.Reconstitute(events);
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
