using Alberto.Dcb;
using Alberto.Payments.Contracts;

namespace Alberto.Payments.Features;

public interface IAuthorizePaymentState
{
    bool Exists { get; }
    bool CanBeAuthorized { get; }
    PaymentStatus Status { get; }
    Guid PaymentId { get; }
}

public sealed partial class PaymentDecider
{
    /// <summary>
    /// Authorizes a payment.
    /// </summary>
    public static Decision Authorize(
        IAuthorizePaymentState state,
        string authorizationCode,
        DateTimeOffset authorizedAt)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeAuthorized)
            return Decision.Fail(PaymentProblems.InvalidStatus("authorized", state.Status));

        if (string.IsNullOrWhiteSpace(authorizationCode))
            return Decision.Fail(PaymentProblems.AuthorizationCodeRequired());

        return Decision.Succeed(new PaymentAuthorized(state.PaymentId, authorizationCode, authorizedAt));
    }

    public PaymentState Apply(PaymentState state, PaymentAuthorized e) => state with
    {
        AuthorizationCode = e.AuthorizationCode,
        AuthorizedAt = e.AuthorizedAt,
        Status = PaymentStatus.Authorized
    };
}
