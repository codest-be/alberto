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
    public static DecisionResult<IEvent> Fail(
        IFailPaymentState state,
        string errorCode,
        string errorMessage)
    {
        if (!state.Exists)
            return DecisionResult<IEvent>.Failure("Payment does not exist");

        if (!state.CanBeFailed)
            return DecisionResult<IEvent>.Failure($"Payment cannot be marked as failed in {state.Status} status");

        if (string.IsNullOrWhiteSpace(errorCode))
            return DecisionResult<IEvent>.Failure("Error code is required");

        return DecisionResult<IEvent>.Success(new PaymentFailed(state.PaymentId, errorCode, errorMessage ?? ""));
    }

    public PaymentState Apply(PaymentState state, PaymentFailed e) => state with
    {
        ErrorCode = e.ErrorCode,
        ErrorMessage = e.ErrorMessage,
        Status = PaymentStatus.Failed
    };
}
