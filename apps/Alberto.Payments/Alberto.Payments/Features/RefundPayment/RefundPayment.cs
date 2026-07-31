using Alberto.Commands;
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

/// <summary>Input for refunding a payment.</summary>
public sealed record RefundPaymentInput(Guid PaymentId, decimal Amount, string Reason);

/// <summary>
/// A refund is bounded by what was captured, not by what was initiated: this slice reads
/// <see cref="CapturedAmount"/> where <c>CapturePayment</c> reads the initiated amount.
/// </summary>
public sealed record RefundPaymentState
{
    public Guid PaymentId { get; init; }
    public decimal? CapturedAmount { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeRefunded => Status == PaymentStatus.Captured;
}

public sealed class RefundPaymentEvolver : Evolver<RefundPaymentState>,
    IEvolve<RefundPaymentState, PaymentInitiated>,
    IEvolve<RefundPaymentState, PaymentAuthorized>,
    IEvolve<RefundPaymentState, PaymentCaptured>,
    IEvolve<RefundPaymentState, PaymentFailed>,
    IEvolve<RefundPaymentState, PaymentRefunded>
{
    public RefundPaymentState Apply(RefundPaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Status = PaymentStatus.Initiated };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentCaptured e) =>
        s with { CapturedAmount = e.CapturedAmount, Status = PaymentStatus.Captured };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class RefundPaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        RefundPaymentState state,
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

        return Decision.Succeed(
            new PaymentRefunded(state.PaymentId, refundedAmount, reason ?? "", refundedAt));
    }
}

public static class RefundPaymentMutation
{
    /// <summary>Refunds a captured payment.</summary>
    [Mutation]
    [GraphQLDescription("Refunds a previously captured payment.")]
    public static async Task<MutationResult> RefundPayment(
        RefundPaymentInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(cmd => RefundPaymentDecider.Boundary(cmd.PaymentId), new RefundPaymentEvolver())
            .Decide((cmd, state) =>
                RefundPaymentDecider.Decide(state, cmd.Amount, cmd.Reason, timeProvider.GetUtcNow()))
            .RetryOnConflict(PaymentSlices.ConflictRetries)
            .Commit(ct)
            .OrThrow();

        return new MutationResult();
    }
}
