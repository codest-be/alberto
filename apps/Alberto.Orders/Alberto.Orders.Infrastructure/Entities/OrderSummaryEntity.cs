using Alberto.Dcb.Subscriptions;
using Alberto.Orders.Core.Order;

namespace Alberto.Orders.Infrastructure.Entities;

/// <summary>
/// EF entity for order summaries with proper relational columns.
/// </summary>
public sealed class OrderSummaryEntity : IProjectionEntity
{
    /// <summary>
    /// Tenant ID for multi-tenant isolation.
    /// </summary>
    public string TenantId { get; set; } = "";

    /// <summary>
    /// Document ID (OrderId as string) for the projection key.
    /// </summary>
    public string DocumentId { get; set; } = "";

    /// <summary>
    /// The order identifier.
    /// </summary>
    public Guid OrderId { get; set; }

    /// <summary>
    /// The customer who placed the order.
    /// </summary>
    public Guid CustomerId { get; set; }

    /// <summary>
    /// Current order status.
    /// </summary>
    public OrderStatus Status { get; set; }

    /// <summary>
    /// Total order amount.
    /// </summary>
    public decimal Total { get; set; }

    /// <summary>
    /// Optional notes for the order.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Tracking number for shipped orders.
    /// </summary>
    public string? TrackingNumber { get; set; }

    /// <summary>
    /// Carrier for shipped orders.
    /// </summary>
    public string? Carrier { get; set; }

    /// <summary>
    /// Reason for cancellation.
    /// </summary>
    public string? CancellationReason { get; set; }

    /// <summary>
    /// When the order was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the order was confirmed.
    /// </summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>
    /// When the order was shipped.
    /// </summary>
    public DateTimeOffset? ShippedAt { get; set; }

    /// <summary>
    /// When the order was delivered.
    /// </summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// When the order was cancelled.
    /// </summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>
    /// When this entity was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Global position of the last event processed for this entity (idempotency).
    /// </summary>
    public long LastProcessedPosition { get; set; }

    /// <summary>
    /// Row version for optimistic concurrency (mapped to PostgreSQL xmin).
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// Line items for this order (stored as JSON).
    /// </summary>
    public List<OrderLineItemData> LineItems { get; set; } = [];
}
