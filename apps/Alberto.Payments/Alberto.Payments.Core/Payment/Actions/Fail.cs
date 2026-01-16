using Alberto.Payments.Core.Events;

namespace Alberto.Payments.Core.Payment.Actions;

public interface IFailPaymentState
{
    bool Exists { get; }
    bool CanBeFailed { get; }
    PaymentStatus Status { get; }
    Guid PaymentId { get; }
}

public sealed partial class PaymentDecider
{
    /// <summary>
    /// Marks a payment as failed.
    /// </summary>
    public static PaymentDecisionResult Fail(
        IFailPaymentState state,
        string errorCode,
        string errorMessage)
    {
        if (!state.Exists)
            return PaymentDecisionResult.Fail("Payment does not exist");

        if (!state.CanBeFailed)
            return PaymentDecisionResult.Fail($"Payment cannot be marked as failed in {state.Status} status");

        if (string.IsNullOrWhiteSpace(errorCode))
            return PaymentDecisionResult.Fail("Error code is required");

        return PaymentDecisionResult.Ok(new PaymentFailed(state.PaymentId, errorCode, errorMessage ?? ""));
    }

    public PaymentState Apply(PaymentState state, PaymentFailed e) => state with
    {
        ErrorCode = e.ErrorCode,
        ErrorMessage = e.ErrorMessage,
        Status = PaymentStatus.Failed
    };
}
