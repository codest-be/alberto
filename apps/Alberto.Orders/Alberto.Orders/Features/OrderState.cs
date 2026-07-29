using Alberto.Orders.Contracts;

namespace Alberto.Orders.Features;

/// <summary>
/// The current state of an order, rebuilt from events.
/// </summary>
public sealed record OrderState :
    IAddItemState,
    IRemoveItemState,
    IConfirmOrderState,
    IShipOrderState,
    IDeliverOrderState,
    ICancelOrderState
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public IReadOnlyList<OrderLineItem> LineItems { get; init; } = [];
    public string? Notes { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;
    public string? TrackingNumber { get; init; }
    public string? Carrier { get; init; }
    public string? CancellationReason { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public DateTimeOffset? ShippedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }

    // Computed properties
    public bool Exists => OrderId != Guid.Empty;
    public bool IsDraft => Status == OrderStatus.Draft;
    public bool IsConfirmed => Status == OrderStatus.Confirmed;
    public bool IsShipped => Status == OrderStatus.Shipped;
    public bool IsDelivered => Status == OrderStatus.Delivered;
    public bool IsCancelled => Status == OrderStatus.Cancelled;
    public bool CanBeModified => IsDraft;
    public bool CanBeConfirmed => IsDraft && LineItems.Count > 0;
    public bool CanBeShipped => IsConfirmed;
    public bool CanBeDelivered => IsShipped;
    public bool CanBeCancelled => IsDraft || IsConfirmed;

    public decimal Total => LineItems.Sum(x => x.Total);
}

