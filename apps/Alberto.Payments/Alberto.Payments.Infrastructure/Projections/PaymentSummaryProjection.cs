using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

/// <summary>
/// Per-payment read model. The document ID is the payment ID, which every payment
/// event carries, so each event routes to its own document without any string matching.
/// </summary>
public static class PaymentSummaryProjection
{
    public static readonly ProjectionDeclaration<PaymentSummary> Declaration =
        DeclareProjection.For<PaymentSummary>(nameof(PaymentSummaryProjection))
            .On<PaymentInitiated>(
                id: e => e.PaymentId.ToString(),
                apply: (_, e, ctx) => new PaymentSummary
                {
                    PaymentId = e.PaymentId,
                    OrderId = e.OrderId,
                    Amount = e.Amount,
                    Currency = e.Currency,
                    PaymentMethod = e.PaymentMethod,
                    Status = PaymentStatus.Initiated,
                    CreatedAt = ctx.Timestamp
                })
            .On<PaymentAuthorized>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => state with
                {
                    Status = PaymentStatus.Authorized,
                    AuthorizationCode = e.AuthorizationCode,
                    AuthorizedAt = e.AuthorizedAt
                })
            .On<PaymentCaptured>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => state with
                {
                    Status = PaymentStatus.Captured,
                    CapturedAt = e.CapturedAt
                })
            .On<PaymentFailed>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => state with
                {
                    Status = PaymentStatus.Failed,
                    ErrorCode = e.ErrorCode,
                    ErrorMessage = e.ErrorMessage
                })
            .On<PaymentRefunded>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => state with
                {
                    Status = PaymentStatus.Refunded,
                    RefundedAmount = e.RefundedAmount,
                    RefundedAt = e.RefundedAt
                })
            .Build();
}
