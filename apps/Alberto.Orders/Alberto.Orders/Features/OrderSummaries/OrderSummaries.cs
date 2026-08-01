using Alberto.Tenancy;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Orders.Features;

/// <summary>
/// GraphQL type for Order.
/// </summary>
/// <remarks>
/// Declared by this slice because this slice's projection is what fills it. <c>GetOrder</c> also
/// returns it — a shared <em>output contract</em> between two read slices, which is a different
/// thing from a shared state record: nothing folds events into an <c>Order</c>.
/// </remarks>
public sealed record Order(
    Guid OrderId,
    Guid CustomerId,
    IReadOnlyList<OrderItem> LineItems,
    string? Notes,
    OrderStatus Status,
    decimal Total,
    string? TrackingNumber,
    string? Carrier,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? UpdatedAt)
{
    public static Order FromEntity(OrderSummaryEntity e) => new(
        e.OrderId,
        e.CustomerId,
        e.LineItems.Select(OrderItem.FromEntity).ToList(),
        e.Notes,
        e.Status,
        e.Total,
        e.TrackingNumber,
        e.Carrier,
        e.CancellationReason,
        e.CreatedAt,
        e.ConfirmedAt,
        e.ShippedAt,
        e.DeliveredAt,
        e.CancelledAt,
        e.UpdatedAt);
}

/// <summary>
/// GraphQL type for order line item.
/// </summary>
public sealed record OrderItem(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal Total)
{
    public static OrderItem FromEntity(OrderLineItemData e) => new(
        e.ProductId,
        e.ProductName,
        e.Quantity,
        e.UnitPrice,
        e.Total);
}

/// <summary>
/// Paginated connection for orders.
/// </summary>
public sealed record OrdersConnection(
    IReadOnlyList<Order> Items,
    int TotalCount,
    int Skip,
    int Take)
{
    public bool HasNextPage => Skip + Take < TotalCount;
    public bool HasPreviousPage => Skip > 0;
}

public static class OrderSummariesQuery
{
    /// <summary>
    /// Gets orders with optional filtering, sorting, and pagination.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets orders with optional filtering by status, customer, and date range.")]
    public static async Task<OrdersConnection> GetOrders(
        [Service] ITenantAccessor tenantAccessor,
        [Service] IDbContextFactory<OrdersDbContext> contextFactory,
        OrderStatus? status = null,
        Guid? customerId = null,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.TenantId;
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var query = dbContext.OrderSummaries
            .Where(o => o.TenantId == tenantId);

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);

        if (customerId.HasValue)
            query = query.Where(o => o.CustomerId == customerId.Value);

        if (createdAfter.HasValue)
            query = query.Where(o => o.CreatedAt >= createdAfter.Value);

        if (createdBefore.HasValue)
            query = query.Where(o => o.CreatedAt <= createdBefore.Value);

        var totalCount = await query.CountAsync(ct);

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
        [Service] ITenantAccessor tenantAccessor,
        [Service] IDbContextFactory<OrdersDbContext> contextFactory,
        int limit = 20,
        CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.TenantId;
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var entities = await dbContext.OrderSummaries
            .Where(o => o.TenantId == tenantId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(Order.FromEntity).ToList();
    }
}
