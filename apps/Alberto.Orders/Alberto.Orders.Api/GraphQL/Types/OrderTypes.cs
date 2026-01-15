using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure.ReadModels;

namespace Alberto.Orders.Api.GraphQL.Types;

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
    public static Order FromSummary(OrderSummary s) => new(
        s.OrderId,
        s.CustomerId,
        s.LineItems.Select(OrderItem.FromSummary).ToList(),
        s.Notes,
        s.Status,
        s.Total,
        s.TrackingNumber,
        s.Carrier,
        s.CancellationReason,
        s.CreatedAt,
        s.ConfirmedAt,
        s.ShippedAt,
        s.DeliveredAt,
        s.CancelledAt,
        s.UpdatedAt);
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
    public static OrderItem FromSummary(OrderLineItemSummary s) => new(
        s.ProductId,
        s.ProductName,
        s.Quantity,
        s.UnitPrice,
        s.Total);
}

/// <summary>
/// Input for creating an order.
/// </summary>
public sealed record CreateOrderInput(
    Guid CustomerId,
    List<OrderItemInput> LineItems,
    string? Notes);

/// <summary>
/// Input for order line items.
/// </summary>
public sealed record OrderItemInput(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

/// <summary>
/// Input for adding an item to an order.
/// </summary>
public sealed record AddOrderItemInput(
    Guid OrderId,
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

/// <summary>
/// Input for shipping an order.
/// </summary>
public sealed record ShipOrderInput(
    Guid OrderId,
    string TrackingNumber,
    string Carrier);

/// <summary>
/// Input for cancelling an order.
/// </summary>
public sealed record CancelOrderInput(
    Guid OrderId,
    string Reason);

/// <summary>
/// Result of a create mutation.
/// </summary>
public readonly record struct CreateOrderResult(Guid OrderId);

/// <summary>
/// Result of a mutation that doesn't return data.
/// </summary>
public readonly record struct MutationResult
{
    public bool Success => true;
}

/// <summary>
/// Error result for failed mutations.
/// </summary>
public sealed record MutationError(string Message);
