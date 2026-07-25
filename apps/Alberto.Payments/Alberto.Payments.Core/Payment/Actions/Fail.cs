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
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeFailed)
            return Decision.Fail(PaymentProblems.InvalidStatus("marked as failed", state.Status));

        if (string.IsNullOrWhiteSpace(errorCode))
            return Decision.Fail(PaymentProblems.ErrorCodeRequired());

        return Decision.Succeed(new PaymentFailed(state.PaymentId, errorCode, errorMessage ?? ""));
    }

    public PaymentState Apply(PaymentState state, PaymentFailed e) => state with
    {
        ErrorCode = e.ErrorCode,
        ErrorMessage = e.ErrorMessage,
        Status = PaymentStatus.Failed
    };
}
