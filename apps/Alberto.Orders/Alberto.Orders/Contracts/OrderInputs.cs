namespace Alberto.Orders.Contracts;

// Staging: Tasks 6–12 move each of these into the slice that is the only thing that names it.

/// <summary>
/// Input for cancelling an order.
/// </summary>
public sealed record CancelOrderInput(
    Guid OrderId,
    string Reason);
