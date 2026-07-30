using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

/// <summary>Input for capturing a payment.</summary>
public sealed record CapturePaymentInput(Guid PaymentId, decimal? Amount);

/// <summary>
/// Capture is the one payment write that needs a figure as well as a status: the amount is both
/// the default when the caller omits one and the ceiling when they supply one.
/// </summary>
public sealed record CapturePaymentState
{
    public Guid PaymentId { get; init; }
    public decimal Amount { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeCaptured => Status == PaymentStatus.Authorized;
}

public sealed class CapturePaymentEvolver : Evolver<CapturePaymentState>,
    IEvolve<CapturePaymentState, PaymentInitiated>,
    IEvolve<CapturePaymentState, PaymentAuthorized>,
    IEvolve<CapturePaymentState, PaymentCaptured>,
    IEvolve<CapturePaymentState, PaymentFailed>,
    IEvolve<CapturePaymentState, PaymentRefunded>
{
    public CapturePaymentState Apply(CapturePaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Amount = e.Amount, Status = PaymentStatus.Initiated };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentCaptured e) =>
        s with { Status = PaymentStatus.Captured };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class CapturePaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        CapturePaymentState state,
        decimal? capturedAmount,
        DateTimeOffset capturedAt)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeCaptured)
            return Decision.Fail(PaymentProblems.InvalidStatus("captured", state.Status));

        var amountToCapture = capturedAmount ?? state.Amount;
        if (amountToCapture <= 0 || amountToCapture > state.Amount)
            return Decision.Fail(PaymentProblems.AmountOutOfRange("Captured", state.Amount));

        return Decision.Succeed(new PaymentCaptured(state.PaymentId, amountToCapture, capturedAt));
    }
}

public static class CapturePaymentMutation
{
    /// <summary>Captures a previously authorized payment.</summary>
    [Mutation]
    [GraphQLDescription("Captures a previously authorized payment.")]
    public static async Task<MutationResult> CapturePayment(
        CapturePaymentInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(cmd => CapturePaymentDecider.Boundary(cmd.PaymentId), new CapturePaymentEvolver())
            .Decide((cmd, state) =>
                CapturePaymentDecider.Decide(state, cmd.Amount, timeProvider.GetUtcNow()))
            .RetryOnConflict(PaymentSlices.ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
