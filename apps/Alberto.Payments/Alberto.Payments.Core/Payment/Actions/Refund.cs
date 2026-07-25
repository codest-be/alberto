using Alberto.Dcb;
using Alberto.Payments.Core.Events;

namespace Alberto.Payments.Core.Payment.Actions;

public interface IRefundPaymentState
{
    bool Exists { get; }
    bool CanBeRefunded { get; }
    PaymentStatus Status { get; }
    Guid PaymentId { get; }
    decimal? CapturedAmount { get; }
}

public sealed partial class PaymentDecider
{
    /// <summary>
    /// Refunds a payment.
    /// </summary>
    public static Decision Refund(
        IRefundPaymentState state,
        decimal refundedAmount,
        string reason,
        DateTimeOffset refundedAt)
    {
        if (!state.Exists)
            return Problem.Create("payment-not-found", "Payment does not exist");

        if (!state.CanBeRefunded)
            return Problem.Create(
                "payment-not-refundable",
                $"Payment cannot be refunded in {state.Status} status");

        var maxRefundable = state.CapturedAmount ?? 0;
        if (refundedAmount <= 0 || refundedAmount > maxRefundable)
            return Problem.Create(
                "invalid-refund-amount",
                $"Refund amount must be between 0 and {maxRefundable}");

        return Decision.Succeed(new PaymentRefunded(state.PaymentId, refundedAmount, reason ?? "", refundedAt));
    }

    public PaymentState Apply(PaymentState state, PaymentRefunded e) => state with
    {
        RefundedAmount = e.RefundedAmount,
        RefundReason = e.Reason,
        RefundedAt = e.RefundedAt,
        Status = PaymentStatus.Refunded
    };
}
