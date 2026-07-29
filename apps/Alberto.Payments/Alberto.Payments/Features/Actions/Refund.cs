using Alberto.Dcb;
using Alberto.Payments.Contracts;

namespace Alberto.Payments.Features;

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
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeRefunded)
            return Decision.Fail(PaymentProblems.InvalidStatus("refunded", state.Status));

        var maxRefundable = state.CapturedAmount ?? 0;
        if (refundedAmount <= 0 || refundedAmount > maxRefundable)
            return Decision.Fail(PaymentProblems.AmountOutOfRange("Refund", maxRefundable));

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
