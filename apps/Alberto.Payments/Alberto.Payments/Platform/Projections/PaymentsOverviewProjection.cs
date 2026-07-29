using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Contracts;

namespace Alberto.Payments.Platform;

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
                id: _ => Overview,
                apply: (state, _, _) => state with { TotalPayments = state.TotalPayments + 1 })
            .On<PaymentAuthorized>(
                id: _ => Overview,
                apply: (state, _, _) => state with { AuthorizedPayments = state.AuthorizedPayments + 1 })
            .On<PaymentCaptured>(
                id: _ => Overview,
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentFailed>(
                id: _ => Overview,
                apply: (state, _, _) => state with { FailedPayments = state.FailedPayments + 1 })
            .On<PaymentRefunded>(
                id: _ => Overview,
                apply: (state, e, _) => Apply(state, e))
            .Build();

    private static PaymentsOverview Apply(PaymentsOverview state, PaymentCaptured e)
        => state with
        {
            CapturedPayments = state.CapturedPayments + 1,
            TotalCapturedAmount = state.TotalCapturedAmount + e.CapturedAmount
        };

    private static PaymentsOverview Apply(PaymentsOverview state, PaymentRefunded e)
        => state with
        {
            RefundedPayments = state.RefundedPayments + 1,
            TotalRefundedAmount = state.TotalRefundedAmount + e.RefundedAmount
        };
}
