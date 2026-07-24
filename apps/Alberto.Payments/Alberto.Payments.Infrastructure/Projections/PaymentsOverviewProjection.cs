using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

/// <summary>
/// Singleton overview of all payment activity. Every event maps to the same document,
/// so the projection is a running set of counters rather than a per-payment read model.
/// </summary>
/// <remarks>
/// Cross-tenant, for the same reason as <c>OrdersOverviewProjection</c>: the async control loop
/// consumes every tenant's events through one singleton state store, and the document-ID selector
/// never sees the tenant. Readers must build their store tenant-agnostically to match.
/// </remarks>
public static class PaymentsOverviewProjection
{
    /// <summary>
    /// The single document this projection maintains.
    /// </summary>
    public const string DocumentId = "overview";

    public static readonly ProjectionDeclaration<PaymentsOverview> Declaration =
        DeclareProjection.For<PaymentsOverview>(nameof(PaymentsOverviewProjection))
            .On<PaymentInitiated>(
                id: _ => DocumentId,
                apply: (state, _, _) => state with { TotalPayments = state.TotalPayments + 1 })
            .On<PaymentAuthorized>(
                id: _ => DocumentId,
                apply: (state, _, _) => state with { AuthorizedPayments = state.AuthorizedPayments + 1 })
            .On<PaymentCaptured>(
                id: _ => DocumentId,
                apply: (state, e, _) => state with
                {
                    CapturedPayments = state.CapturedPayments + 1,
                    TotalCapturedAmount = state.TotalCapturedAmount + e.CapturedAmount
                })
            .On<PaymentFailed>(
                id: _ => DocumentId,
                apply: (state, _, _) => state with { FailedPayments = state.FailedPayments + 1 })
            .On<PaymentRefunded>(
                id: _ => DocumentId,
                apply: (state, e, _) => state with
                {
                    RefundedPayments = state.RefundedPayments + 1,
                    TotalRefundedAmount = state.TotalRefundedAmount + e.RefundedAmount
                })
            .Build();
}
