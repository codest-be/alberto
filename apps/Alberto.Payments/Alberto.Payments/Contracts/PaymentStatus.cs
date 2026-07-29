namespace Alberto.Payments.Contracts;

/// <summary>
/// Possible states of a payment.
/// </summary>
/// <remarks>
/// A contract, not state: the read model persists it, the GraphQL schema exposes it by these
/// names, and several slices compare against it. Each slice still folds its own status from the
/// events — this only fixes what the values are called.
/// </remarks>
public enum PaymentStatus
{
    None = 0,
    Initiated = 1,
    Authorized = 2,
    Captured = 3,
    Failed = 4,
    Refunded = 5
}
