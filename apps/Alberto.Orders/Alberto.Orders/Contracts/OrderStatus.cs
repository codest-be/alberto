namespace Alberto.Orders.Contracts;

/// <summary>
/// Possible states of an order.
/// </summary>
/// <remarks>
/// A contract, not state: the EF projection persists it, the GraphQL schema exposes it by these
/// names, and several slices compare against it. Each slice still folds its own status from the
/// events — this only fixes what the values are called.
/// </remarks>
public enum OrderStatus
{
    None = 0,
    Draft = 1,
    Confirmed = 2,
    Shipped = 3,
    Delivered = 4,
    Cancelled = 5
}
