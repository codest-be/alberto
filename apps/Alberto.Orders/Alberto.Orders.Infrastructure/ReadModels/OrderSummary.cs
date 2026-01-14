using Alberto.Orders.Core;

namespace Alberto.Orders.Infrastructure.ReadModels;

/// <summary>
/// Read model for order queries.
/// </summary>
public sealed record OrderSummary
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public List<OrderLineItemSummary> LineItems { get; init; } = [];
    public string? Notes { get; init; }
    public OrderStatus Status { get; init; }
    public decimal Total { get; init; }
    public string? TrackingNumber { get; init; }
    public string? Carrier { get; init; }
    public string? CancellationReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public DateTimeOffset? ShippedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Read model for order line items.
/// </summary>
public sealed record OrderLineItemSummary
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Total { get; init; }
}
