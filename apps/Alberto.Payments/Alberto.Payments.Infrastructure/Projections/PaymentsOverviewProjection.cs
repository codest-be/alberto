using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

public static class PaymentsOverviewProjection
{
    public static readonly ProjectionDeclaration<PaymentsOverview> Declaration =
        DeclareProjection.For<PaymentsOverview>(nameof(PaymentsOverviewProjection))
            .Handles<PaymentInitiated>()
            .Handles<PaymentAuthorized>()
            .Handles<PaymentCaptured>()
            .Handles<PaymentFailed>()
            .Handles<PaymentRefunded>()
            .DocumentId(_ => "overview")   // singleton, all events map to same doc
            .Evolve(Evolve)
            .Build();

    private static ProjectionResult<PaymentsOverview> Evolve(PaymentsOverview state, IEventEnvelope e, ProjectionContext ctx)
        => e.EventType.Id switch
        {
            "payment-initiated" => state with { TotalPayments = state.TotalPayments + 1 },
            "payment-authorized" => state with { AuthorizedPayments = state.AuthorizedPayments + 1 },
            "payment-captured" => Apply(state, e.ParseEvent<PaymentCaptured>()!),
            "payment-failed" => state with { FailedPayments = state.FailedPayments + 1 },
            "payment-refunded" => Apply(state, e.ParseEvent<PaymentRefunded>()!),
            _ => state
        };

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
