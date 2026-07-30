using Alberto.Payments.Contracts;

namespace Alberto.Payments.Features;

public sealed partial class PaymentDecider
{
    public PaymentState Apply(PaymentState state, PaymentFailed e) => state with
    {
        ErrorCode = e.ErrorCode,
        ErrorMessage = e.ErrorMessage,
        Status = PaymentStatus.Failed
    };
}
