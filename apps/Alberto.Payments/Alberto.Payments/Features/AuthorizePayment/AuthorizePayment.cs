using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

/// <summary>
/// Authorization needs the payment's identity and status. Amount, currency, method and the
/// capture/refund figures belong to other slices.
/// </summary>
public sealed record AuthorizePaymentState
{
    public Guid PaymentId { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeAuthorized => Status == PaymentStatus.Initiated;
}

public sealed class AuthorizePaymentEvolver : Evolver<AuthorizePaymentState>,
    IEvolve<AuthorizePaymentState, PaymentInitiated>,
    IEvolve<AuthorizePaymentState, PaymentAuthorized>,
    IEvolve<AuthorizePaymentState, PaymentCaptured>,
    IEvolve<AuthorizePaymentState, PaymentFailed>,
    IEvolve<AuthorizePaymentState, PaymentRefunded>
{
    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Status = PaymentStatus.Initiated };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentCaptured e) =>
        s with { Status = PaymentStatus.Captured };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class AuthorizePaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        AuthorizePaymentState state,
        string authorizationCode,
        DateTimeOffset authorizedAt)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeAuthorized)
            return Decision.Fail(PaymentProblems.InvalidStatus("authorized", state.Status));

        if (string.IsNullOrWhiteSpace(authorizationCode))
            return Decision.Fail(PaymentProblems.AuthorizationCodeRequired());

        return Decision.Succeed(
            new PaymentAuthorized(state.PaymentId, authorizationCode, authorizedAt));
    }
}

public static class AuthorizePaymentMutation
{
    /// <summary>Authorizes a payment.</summary>
    [Mutation]
    [GraphQLDescription("Authorizes a payment with an authorization code.")]
    public static async Task<MutationResult> AuthorizePayment(
        Guid paymentId,
        string authorizationCode,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(authorizationCode)
            .Load(AuthorizePaymentDecider.Boundary(paymentId), new AuthorizePaymentEvolver())
            .Decide((code, state) =>
                AuthorizePaymentDecider.Decide(state, code, timeProvider.GetUtcNow()))
            .RetryOnConflict(PaymentSlices.ConflictRetries)
            .Commit(ct)
            .OrThrow();

        return new MutationResult();
    }
}
