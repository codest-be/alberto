using Alberto.Dcb;

namespace Alberto.Payments.Core.Payment;

/// <summary>
/// Decider for payment operations. Contains business logic as pure functions.
/// </summary>
public sealed partial class PaymentDecider
{
    /// <summary>
    /// Gets the DCB query for a payment's consistency boundary.
    /// </summary>
    public static DcbQuery BoundaryFor(Guid paymentId) =>
        DcbQuery.For(Tags.Payment, paymentId);
}
