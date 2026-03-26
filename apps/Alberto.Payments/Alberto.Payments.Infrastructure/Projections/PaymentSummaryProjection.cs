using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

public static class PaymentSummaryProjection
{
    public static readonly ProjectionDeclaration<PaymentSummary> Declaration =
        DeclareProjection.For<PaymentSummary>(nameof(PaymentSummaryProjection))
            .Handles<PaymentInitiated>()
            .Handles<PaymentAuthorized>()
            .Handles<PaymentCaptured>()
            .Handles<PaymentFailed>()
            .Handles<PaymentRefunded>()
            .DocumentId(e => e.EventType.Id switch
            {
                "payment-initiated" => e.ParseEvent<PaymentInitiated>()!.PaymentId.ToString(),
                "payment-authorized" => e.ParseEvent<PaymentAuthorized>()!.PaymentId.ToString(),
                "payment-captured" => e.ParseEvent<PaymentCaptured>()!.PaymentId.ToString(),
                "payment-failed" => e.ParseEvent<PaymentFailed>()!.PaymentId.ToString(),
                "payment-refunded" => e.ParseEvent<PaymentRefunded>()!.PaymentId.ToString(),
                _ => null
            })
            .Evolve(Evolve)
            .Build();

    private static ProjectionResult<PaymentSummary> Evolve(PaymentSummary state, IEventEnvelope e, ProjectionContext ctx)
        => e.EventType.Id switch
        {
            "payment-initiated" => Apply(state, e.ParseEvent<PaymentInitiated>()!, ctx),
            "payment-authorized" => Apply(state, e.ParseEvent<PaymentAuthorized>()!),
            "payment-captured" => Apply(state, e.ParseEvent<PaymentCaptured>()!),
            "payment-failed" => Apply(state, e.ParseEvent<PaymentFailed>()!),
            "payment-refunded" => Apply(state, e.ParseEvent<PaymentRefunded>()!),
            _ => state
        };

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
