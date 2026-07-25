using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

/// <summary>
/// One row per payment, tracking where it got to in the authorize/capture/refund lifecycle.
/// </summary>
public static class PaymentSummaryProjection
{
    public static readonly ProjectionDeclaration<PaymentSummary> Declaration =
        DeclareProjection.For<PaymentSummary>(nameof(PaymentSummaryProjection))
            .On<PaymentInitiated>(
                e => e.PaymentId.ToString(),
                (_, e, ctx) => new PaymentSummary
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
                e => e.PaymentId.ToString(),
                (state, e, _) => state with
                {
                    Status = PaymentStatus.Authorized,
                    AuthorizationCode = e.AuthorizationCode,
                    AuthorizedAt = e.AuthorizedAt
                })
            .On<PaymentCaptured>(
                e => e.PaymentId.ToString(),
                (state, e, _) => state with { Status = PaymentStatus.Captured, CapturedAt = e.CapturedAt })
            .On<PaymentFailed>(
                e => e.PaymentId.ToString(),
                (state, e, _) => state with
                {
                    Status = PaymentStatus.Failed,
                    ErrorCode = e.ErrorCode,
                    ErrorMessage = e.ErrorMessage
                })
            .On<PaymentRefunded>(
                e => e.PaymentId.ToString(),
                (state, e, _) => state with
                {
                    Status = PaymentStatus.Refunded,
                    RefundedAmount = e.RefundedAmount,
                    RefundedAt = e.RefundedAt
                })
            .Build();
}
