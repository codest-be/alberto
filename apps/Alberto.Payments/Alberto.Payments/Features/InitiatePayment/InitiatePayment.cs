using Alberto.Commands;
using Alberto;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

/// <summary>Input for initiating a payment.</summary>
public sealed record InitiatePaymentInput(
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod);

/// <summary>Result of initiating a payment.</summary>
public readonly record struct InitiatePaymentResult(Guid PaymentId);

/// <summary>
/// Initiation decides on existence alone: a second PaymentInitiated is refused whatever
/// status, amount or currency the first one carried.
/// </summary>
public sealed record InitiatePaymentState
{
    public Guid PaymentId { get; init; }

    public bool Exists => PaymentId != Guid.Empty;
}

public sealed class InitiatePaymentEvolver : Evolver<InitiatePaymentState>,
    IEvolve<InitiatePaymentState, PaymentInitiated>
{
    public InitiatePaymentState Apply(InitiatePaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId };
}

public static class InitiatePaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        InitiatePaymentState state,
        Guid paymentId,
        Guid orderId,
        decimal amount,
        string currency,
        string paymentMethod)
    {
        if (state.Exists)
            return Decision.Fail(PaymentProblems.AlreadyExists(paymentId));

        if (orderId == Guid.Empty)
            return Decision.Fail(PaymentProblems.OrderRequired());

        if (amount <= 0)
            return Decision.Fail(PaymentProblems.InvalidAmount());

        if (string.IsNullOrWhiteSpace(currency))
            return Decision.Fail(PaymentProblems.CurrencyRequired());

        if (string.IsNullOrWhiteSpace(paymentMethod))
            return Decision.Fail(PaymentProblems.PaymentMethodRequired());

        return Decision.Succeed(
            new PaymentInitiated(paymentId, orderId, amount, currency, paymentMethod));
    }
}

public static class InitiatePaymentMutation
{
    /// <summary>Initiates a new payment for an order.</summary>
    [Mutation]
    [GraphQLDescription("Initiates a new payment for an order.")]
    public static async Task<InitiatePaymentResult> InitiatePayment(
        InitiatePaymentInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var paymentId = Guid.CreateVersion7();

        // No RetryOnConflict: a conflict here means someone else claimed this id, and re-deciding
        // would only refuse it again.
        await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(InitiatePaymentDecider.Boundary(paymentId), new InitiatePaymentEvolver())
            .Decide((cmd, state) => InitiatePaymentDecider.Decide(
                state, paymentId, cmd.OrderId, cmd.Amount, cmd.Currency, cmd.PaymentMethod))
            .Commit(ct)
            .OrThrow();

        return new InitiatePaymentResult(paymentId);
    }
}
