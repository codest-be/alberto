namespace Alberto.Orders.Contracts;

// Staging: Tasks 6–12 move each of these into the slice that is the only thing that names it.

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
