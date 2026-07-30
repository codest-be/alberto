using Alberto.Payments.Contracts;

namespace Alberto.Payments.Features;

public sealed partial class PaymentDecider
{
    public PaymentState Apply(PaymentState state, PaymentRefunded e) => state with
    {
        RefundedAmount = e.RefundedAmount,
        RefundReason = e.Reason,
        RefundedAt = e.RefundedAt,
        Status = PaymentStatus.Refunded
    };
}
