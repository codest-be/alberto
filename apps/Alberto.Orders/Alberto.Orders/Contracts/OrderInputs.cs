namespace Alberto.Orders.Contracts;

// Staging: Tasks 6–12 move each of these into the slice that is the only thing that names it.

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
