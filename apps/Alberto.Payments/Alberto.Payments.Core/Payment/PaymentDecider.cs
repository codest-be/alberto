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

/// <summary>
/// Result of a payment decision operation.
/// </summary>
public readonly struct PaymentDecisionResult
{
    public bool IsSuccess { get; }
    public IEvent? Event { get; }
    public string? Error { get; }

    private PaymentDecisionResult(bool isSuccess, IEvent? @event, string? error)
    {
        IsSuccess = isSuccess;
        Event = @event;
        Error = error;
    }

    public static PaymentDecisionResult Ok(IEvent @event) => new(true, @event, null);
    public static PaymentDecisionResult Fail(string error) => new(false, null, error);

    public void EnsureSuccess()
    {
        if (!IsSuccess)
            throw new InvalidOperationException(Error);
    }
}
