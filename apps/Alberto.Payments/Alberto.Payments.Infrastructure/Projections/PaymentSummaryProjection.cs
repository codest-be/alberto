using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

public static class PaymentSummaryProjection
{
    public static readonly ProjectionDeclaration<PaymentSummary> Declaration =
        DeclareProjection.For<PaymentSummary>(nameof(PaymentSummaryProjection))
            .On<PaymentInitiated>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, ctx) => Apply(state, e, ctx))
            .On<PaymentAuthorized>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentCaptured>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentFailed>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentRefunded>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .Build();

    private static PaymentSummary Apply(PaymentSummary state, PaymentInitiated e, ProjectionContext ctx)
        => new PaymentSummary
        {
            PaymentId = e.PaymentId,
            OrderId = e.OrderId,
            Amount = e.Amount,
            Currency = e.Currency,
            PaymentMethod = e.PaymentMethod,
            Status = PaymentStatus.Initiated,
            CreatedAt = ctx.Timestamp
        };

    private static PaymentSummary Apply(PaymentSummary state, PaymentAuthorized e)
        => state with
        {
            Status = PaymentStatus.Authorized,
            AuthorizationCode = e.AuthorizationCode,
            AuthorizedAt = e.AuthorizedAt
        };

    private static PaymentSummary Apply(PaymentSummary state, PaymentCaptured e)
        => state with { Status = PaymentStatus.Captured, CapturedAt = e.CapturedAt };

    private static PaymentSummary Apply(PaymentSummary state, PaymentFailed e)
        => state with
        {
            Status = PaymentStatus.Failed,
            ErrorCode = e.ErrorCode,
            ErrorMessage = e.ErrorMessage
        };

    private static PaymentSummary Apply(PaymentSummary state, PaymentRefunded e)
        => state with
        {
            Status = PaymentStatus.Refunded,
            RefundedAmount = e.RefundedAmount,
            RefundedAt = e.RefundedAt
        };
}
