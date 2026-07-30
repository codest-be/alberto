namespace Alberto.Payments.Features;

/// <summary>
/// The one setting every payment write slice shares.
/// </summary>
/// <remarks>
/// Not state: a retry count is a policy the module picked once, and duplicating the literal
/// across five slices would let them drift apart for no reason. Slices still each decide
/// whether to retry at all — <c>InitiatePayment</c> does not.
/// </remarks>
public static class PaymentSlices
{
    /// <summary>How many times a conflicted append is re-read and re-decided before giving up.</summary>
    public const int ConflictRetries = 3;
}
