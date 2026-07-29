using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Payments.Core.Payment;
using Alberto.Payments.Infrastructure;
using PaymentActions = Alberto.Payments.Core.Payment.Actions.PaymentDecider;
using PaymentBoundary = Alberto.Payments.Core.Payment.PaymentDecider;

namespace Alberto.Orders.Api.GraphQL.Mutations;

/// <summary>
/// GraphQL mutations for payment operations.
/// </summary>
/// <inheritdoc cref="OrderMutations" path="/remarks"/>
public static class PaymentMutations
{
    private static readonly PaymentEvolver _evolver = new();

    /// <summary>How many times a conflicted append is re-read and re-decided before giving up.</summary>
    private const int ConflictRetries = 3;

    /// <summary>
    /// Initiates a new payment for an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Initiates a new payment for an order.")]
    public static async Task<InitiatePaymentResult> InitiatePayment(
        InitiatePaymentInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var paymentId = Guid.CreateVersion7();

        var result = await Store(sp)
            .Handle(input)
            .Load(PaymentBoundary.BoundaryFor(paymentId), _evolver)
            .Decide((cmd, state) => PaymentActions.Initiate(
                state, paymentId, cmd.OrderId, cmd.Amount, cmd.Currency, cmd.PaymentMethod))
            .Commit(ct);

        result.EnsureCommitted();
        return new InitiatePaymentResult(paymentId);
    }

    /// <summary>
    /// Authorizes a payment.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Authorizes a payment with an authorization code.")]
    public static async Task<MutationResult> AuthorizePayment(
        Guid paymentId,
        string authorizationCode,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(authorizationCode)
            .Load(PaymentBoundary.BoundaryFor(paymentId), _evolver)
            .Decide((code, state) => PaymentActions.Authorize(state, code, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Captures a payment.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Captures a previously authorized payment.")]
    public static async Task<MutationResult> CapturePayment(
        CapturePaymentInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(input)
            .Load(cmd => PaymentBoundary.BoundaryFor(cmd.PaymentId), _evolver)
            .Decide((cmd, state) => PaymentActions.Capture(state, cmd.Amount, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Marks a payment as failed.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Marks a payment as failed.")]
    public static async Task<MutationResult> FailPayment(
        FailPaymentInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(input)
            .Load(cmd => PaymentBoundary.BoundaryFor(cmd.PaymentId), _evolver)
            .Decide((cmd, state) => PaymentActions.Fail(state, cmd.ErrorCode, cmd.ErrorMessage))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Refunds a captured payment.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Refunds a previously captured payment.")]
    public static async Task<MutationResult> RefundPayment(
        RefundPaymentInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(input)
            .Load(cmd => PaymentBoundary.BoundaryFor(cmd.PaymentId), _evolver)
            .Decide((cmd, state) =>
                PaymentActions.Refund(state, cmd.Amount, cmd.Reason, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    private static AlbertoStore Store(IServiceProvider sp) =>
        sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey);
}
