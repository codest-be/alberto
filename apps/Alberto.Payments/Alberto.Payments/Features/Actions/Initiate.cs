using Alberto.Payments.Contracts;

namespace Alberto.Payments.Features;

public sealed partial class PaymentDecider
{
    public PaymentState Apply(PaymentState state, PaymentInitiated e) => state with
    {
        PaymentId = e.PaymentId,
        OrderId = e.OrderId,
        Amount = e.Amount,
        Currency = e.Currency,
        PaymentMethod = e.PaymentMethod,
        Status = PaymentStatus.Initiated
    };
}
