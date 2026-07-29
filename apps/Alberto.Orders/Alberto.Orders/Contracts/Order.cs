using Alberto.Orders.Platform;

namespace Alberto.Orders.Contracts;

/// <summary>
/// GraphQL type for Order.
/// </summary>
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
