using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

public static class PaymentsOverviewProjection
{
    public static readonly ProjectionDeclaration<PaymentsOverview> Declaration =
        DeclareProjection.For<PaymentsOverview>(nameof(PaymentsOverviewProjection))
            .On<PaymentInitiated>(
                id: _ => "overview",
                apply: (state, _, _) => state with { TotalPayments = state.TotalPayments + 1 })
            .On<PaymentAuthorized>(
                id: _ => "overview",
                apply: (state, _, _) => state with { AuthorizedPayments = state.AuthorizedPayments + 1 })
            .On<PaymentCaptured>(
                id: _ => "overview",
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentFailed>(
                id: _ => "overview",
                apply: (state, _, _) => state with { FailedPayments = state.FailedPayments + 1 })
            .On<PaymentRefunded>(
                id: _ => "overview",
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
