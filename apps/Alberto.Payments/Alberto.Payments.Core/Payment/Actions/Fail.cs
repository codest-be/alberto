using Alberto.Dcb;
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
    public static Decision Fail(
        IFailPaymentState state,
        string errorCode,
        string errorMessage)
    {
        if (!state.Exists)
            return Problem.Create("payment-not-found", "Payment does not exist");

        if (!state.CanBeFailed)
            return Problem.Create(
                "payment-not-failable",
                $"Payment cannot be marked as failed in {state.Status} status");

        if (string.IsNullOrWhiteSpace(errorCode))
            return Problem.Create("error-code-required", "Error code is required");

        return Decision.Succeed(new PaymentFailed(state.PaymentId, errorCode, errorMessage ?? ""));
    }

    public PaymentState Apply(PaymentState state, PaymentFailed e) => state with
    {
        ErrorCode = e.ErrorCode,
        ErrorMessage = e.ErrorMessage,
        Status = PaymentStatus.Failed
    };
}
