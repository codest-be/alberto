using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

/// <summary>
/// Payment totals across the whole system, folded into a single document.
/// </summary>
public static class PaymentsOverviewProjection
{
    // Every event lands on the same document: this projection is one row, not one per payment.
    private const string Overview = "overview";

    public static readonly ProjectionDeclaration<PaymentsOverview> Declaration =
        DeclareProjection.For<PaymentsOverview>(nameof(PaymentsOverviewProjection))
            .On<PaymentInitiated>(
                _ => Overview,
                (state, _, _) => state with { TotalPayments = state.TotalPayments + 1 })
            .On<PaymentAuthorized>(
                _ => Overview,
                (state, _, _) => state with { AuthorizedPayments = state.AuthorizedPayments + 1 })
            .On<PaymentCaptured>(
                _ => Overview,
                (state, e, _) => state with
                {
                    CapturedPayments = state.CapturedPayments + 1,
                    TotalCapturedAmount = state.TotalCapturedAmount + e.CapturedAmount
                })
            .On<PaymentFailed>(
                _ => Overview,
                (state, _, _) => state with { FailedPayments = state.FailedPayments + 1 })
            .On<PaymentRefunded>(
                _ => Overview,
                (state, e, _) => state with
                {
                    RefundedPayments = state.RefundedPayments + 1,
                    TotalRefundedAmount = state.TotalRefundedAmount + e.RefundedAmount
                })
            .Build();
}
