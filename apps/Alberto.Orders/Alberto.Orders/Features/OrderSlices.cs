namespace Alberto.Orders.Features;

/// <summary>
/// The one setting every order write slice shares.
/// </summary>
/// <remarks>
/// Not state: a retry count is a policy the module picked once, and duplicating the literal
/// across seven slices would let them drift apart for no reason. Slices still each decide
/// whether to retry at all — <c>CreateOrder</c> does not.
/// </remarks>
public static class OrderSlices
{
    /// <summary>How many times a conflicted append is re-read and re-decided before giving up.</summary>
    public const int ConflictRetries = 3;
}
