using Alberto.Payments.Contracts;

namespace Alberto.Payments.Features;

public sealed partial class PaymentDecider
{
    public PaymentState Apply(PaymentState state, PaymentCaptured e) => state with
    {
        CapturedAmount = e.CapturedAmount,
        CapturedAt = e.CapturedAt,
        Status = PaymentStatus.Captured
    };
}
