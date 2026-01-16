using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

/// <summary>
/// Projects payment events into a PaymentSummary read model.
/// </summary>
public sealed class PaymentSummaryProjection : Projection<PaymentSummary>,
    IProject<PaymentSummary, PaymentInitiated>,
    IProject<PaymentSummary, PaymentAuthorized>,
    IProject<PaymentSummary, PaymentCaptured>,
    IProject<PaymentSummary, PaymentFailed>,
    IProject<PaymentSummary, PaymentRefunded>
{
    public string GetDocumentId(PaymentInitiated e) => e.PaymentId.ToString();
    public ProjectionResult<PaymentSummary> Apply(PaymentSummary state, PaymentInitiated e, ProjectionContext ctx)
    {
        return new PaymentSummary
        {
            PaymentId = e.PaymentId,
            OrderId = e.OrderId,
            Amount = e.Amount,
            Currency = e.Currency,
            PaymentMethod = e.PaymentMethod,
            Status = PaymentStatus.Initiated,
            CreatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(PaymentAuthorized e) => e.PaymentId.ToString();
    public ProjectionResult<PaymentSummary> Apply(PaymentSummary state, PaymentAuthorized e, ProjectionContext ctx)
    {
        if (state is null) return ProjectionResults.Unchanged<PaymentSummary>();
        state.Status = PaymentStatus.Authorized;
        state.AuthorizationCode = e.AuthorizationCode;
        state.AuthorizedAt = e.AuthorizedAt;
        return state;
    }

    public string GetDocumentId(PaymentCaptured e) => e.PaymentId.ToString();
    public ProjectionResult<PaymentSummary> Apply(PaymentSummary state, PaymentCaptured e, ProjectionContext ctx)
    {
        if (state is null) return ProjectionResults.Unchanged<PaymentSummary>();
        state.Status = PaymentStatus.Captured;
        state.CapturedAt = e.CapturedAt;
        return state;
    }

    public string GetDocumentId(PaymentFailed e) => e.PaymentId.ToString();
    public ProjectionResult<PaymentSummary> Apply(PaymentSummary state, PaymentFailed e, ProjectionContext ctx)
    {
        if (state is null) return ProjectionResults.Unchanged<PaymentSummary>();
        state.Status = PaymentStatus.Failed;
        state.ErrorCode = e.ErrorCode;
        state.ErrorMessage = e.ErrorMessage;
        return state;
    }

    public string GetDocumentId(PaymentRefunded e) => e.PaymentId.ToString();
    public ProjectionResult<PaymentSummary> Apply(PaymentSummary state, PaymentRefunded e, ProjectionContext ctx)
    {
        if (state is null) return ProjectionResults.Unchanged<PaymentSummary>();
        state.Status = PaymentStatus.Refunded;
        state.RefundedAmount = e.RefundedAmount;
        state.RefundedAt = e.RefundedAt;
        return state;
    }
}
