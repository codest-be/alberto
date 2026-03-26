using Alberto.Dcb;
using Alberto.Payments.Core.Events;

namespace Alberto.Payments.Core.Payment.Actions;

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
    public static DecisionResult<IEvent> Authorize(
        IAuthorizePaymentState state,
        string authorizationCode,
        DateTimeOffset authorizedAt)
    {
        if (!state.Exists)
            return DecisionResult<IEvent>.Failure("Payment does not exist");

        if (!state.CanBeAuthorized)
            return DecisionResult<IEvent>.Failure($"Payment cannot be authorized in {state.Status} status");

        if (string.IsNullOrWhiteSpace(authorizationCode))
            return DecisionResult<IEvent>.Failure("Authorization code is required");

        return DecisionResult<IEvent>.Success(new PaymentAuthorized(state.PaymentId, authorizationCode, authorizedAt));
    }

    public PaymentState Apply(PaymentState state, PaymentAuthorized e) => state with
    {
        AuthorizationCode = e.AuthorizationCode,
        AuthorizedAt = e.AuthorizedAt,
        Status = PaymentStatus.Authorized
    };
}
