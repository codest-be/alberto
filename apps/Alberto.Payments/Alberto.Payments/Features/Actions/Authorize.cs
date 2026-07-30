using Alberto.Payments.Contracts;

namespace Alberto.Payments.Features;

public sealed partial class PaymentDecider
{
    public PaymentState Apply(PaymentState state, PaymentAuthorized e) => state with
    {
        AuthorizationCode = e.AuthorizationCode,
        AuthorizedAt = e.AuthorizedAt,
        Status = PaymentStatus.Authorized
    };
}
