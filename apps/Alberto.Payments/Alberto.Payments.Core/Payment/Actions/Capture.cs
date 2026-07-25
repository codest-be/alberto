using Alberto.Dcb;
using Alberto.Payments.Core.Events;

namespace Alberto.Payments.Core.Payment.Actions;

public interface ICapturePaymentState
{
    bool Exists { get; }
    bool CanBeCaptured { get; }
    PaymentStatus Status { get; }
    Guid PaymentId { get; }
    decimal Amount { get; }
}

public sealed partial class PaymentDecider
{
    /// <summary>
    /// Captures a payment.
    /// </summary>
    public static Decision Capture(
        ICapturePaymentState state,
        decimal? capturedAmount,
        DateTimeOffset capturedAt)
    {
        if (!state.Exists)
            return Problem.Create("payment-not-found", "Payment does not exist");

        if (!state.CanBeCaptured)
            return Problem.Create(
                "payment-not-capturable",
                $"Payment cannot be captured in {state.Status} status");

        var amountToCapture = capturedAmount ?? state.Amount;
        if (amountToCapture <= 0 || amountToCapture > state.Amount)
            return Problem.Create(
                "invalid-capture-amount",
                $"Captured amount must be between 0 and {state.Amount}");

        return Decision.Succeed(new PaymentCaptured(state.PaymentId, amountToCapture, capturedAt));
    }

    public PaymentState Apply(PaymentState state, PaymentCaptured e) => state with
    {
        CapturedAmount = e.CapturedAmount,
        CapturedAt = e.CapturedAt,
        Status = PaymentStatus.Captured
    };
}
