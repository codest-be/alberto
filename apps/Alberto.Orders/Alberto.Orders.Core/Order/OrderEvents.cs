using Alberto.Dcb;

namespace Alberto.Orders.Core.Order;

/// <summary>
/// Order was created with initial line items.
/// </summary>
[EventType("order-created")]
public sealed record OrderCreated(
    [property: Tag(Tags.Order)] Guid OrderId,
    Guid CustomerId,
    IReadOnlyList<OrderLineItem> LineItems,
    string? Notes) : IEvent;

/// <summary>
/// A line item was added to the order.
/// </summary>
[EventType("order-item-added")]
public sealed record OrderItemAdded(
    [property: Tag(Tags.Order)] Guid OrderId,
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice) : IEvent;

/// <summary>
/// A line item was removed from the order.
/// </summary>
[EventType("order-item-removed")]
public sealed record OrderItemRemoved(
    [property: Tag(Tags.Order)] Guid OrderId,
    Guid ProductId) : IEvent;

/// <summary>
/// Order was confirmed and is ready for processing.
/// </summary>
[EventType("order-confirmed")]
public sealed record OrderConfirmed(
    [property: Tag(Tags.Order)] Guid OrderId,
    DateTimeOffset ConfirmedAt) : IEvent;

/// <summary>
/// Order was shipped to the customer.
/// </summary>
[EventType("order-shipped")]
public sealed record OrderShipped(
    [property: Tag(Tags.Order)] Guid OrderId,
    string TrackingNumber,
    string Carrier,
    DateTimeOffset ShippedAt) : IEvent;

/// <summary>
/// Order was delivered to the customer.
/// </summary>
[EventType("order-delivered")]
public sealed record OrderDelivered(
    [property: Tag(Tags.Order)] Guid OrderId,
    DateTimeOffset DeliveredAt) : IEvent;

/// <summary>
/// Order was cancelled.
/// </summary>
[EventType("order-cancelled")]
public sealed record OrderCancelled(
    [property: Tag(Tags.Order)] Guid OrderId,
    string Reason,
    DateTimeOffset CancelledAt) : IEvent;

/// <summary>
/// Represents a line item in an order.
/// </summary>
public sealed record OrderLineItem(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice)
{
    public decimal Total => Quantity * UnitPrice;
}
