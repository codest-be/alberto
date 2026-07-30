using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

/// <summary>Input for failing a payment.</summary>
public sealed record FailPaymentInput(Guid PaymentId, string ErrorCode, string ErrorMessage);

/// <summary>
/// Failing a payment turns on identity and status alone. The error code and message it records
/// come from the caller, so nothing about a previous failure needs replaying.
/// </summary>
public sealed record FailPaymentState
{
    public Guid PaymentId { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeFailed => Status is PaymentStatus.Initiated or PaymentStatus.Authorized;
}

public sealed class FailPaymentEvolver : Evolver<FailPaymentState>,
    IEvolve<FailPaymentState, PaymentInitiated>,
    IEvolve<FailPaymentState, PaymentAuthorized>,
    IEvolve<FailPaymentState, PaymentCaptured>,
    IEvolve<FailPaymentState, PaymentFailed>,
    IEvolve<FailPaymentState, PaymentRefunded>
{
    public FailPaymentState Apply(FailPaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Status = PaymentStatus.Initiated };

    public FailPaymentState Apply(FailPaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public FailPaymentState Apply(FailPaymentState s, PaymentCaptured e) =>
        s with { Status = PaymentStatus.Captured };

    public FailPaymentState Apply(FailPaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public FailPaymentState Apply(FailPaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class FailPaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(FailPaymentState state, string errorCode, string errorMessage)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeFailed)
            return Decision.Fail(PaymentProblems.InvalidStatus("marked as failed", state.Status));

        if (string.IsNullOrWhiteSpace(errorCode))
            return Decision.Fail(PaymentProblems.ErrorCodeRequired());

        return Decision.Succeed(new PaymentFailed(state.PaymentId, errorCode, errorMessage ?? ""));
    }
}

public static class FailPaymentMutation
{
    /// <summary>Marks a payment as failed.</summary>
    [Mutation]
    [GraphQLDescription("Marks a payment as failed.")]
    public static async Task<MutationResult> FailPayment(
        FailPaymentInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(cmd => FailPaymentDecider.Boundary(cmd.PaymentId), new FailPaymentEvolver())
            .Decide((cmd, state) =>
                FailPaymentDecider.Decide(state, cmd.ErrorCode, cmd.ErrorMessage))
            .RetryOnConflict(PaymentSlices.ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
