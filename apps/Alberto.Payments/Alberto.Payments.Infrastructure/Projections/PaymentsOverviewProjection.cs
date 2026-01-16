using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Infrastructure.ReadModels;

namespace Alberto.Payments.Infrastructure.Projections;

/// <summary>
/// Projects payment events into an aggregate PaymentsOverview read model.
/// Uses a singleton document ID to maintain a single aggregate view.
/// </summary>
public sealed class PaymentsOverviewProjection : Projection<PaymentsOverview>,
    IProject<PaymentsOverview, PaymentInitiated>,
    IProject<PaymentsOverview, PaymentAuthorized>,
    IProject<PaymentsOverview, PaymentCaptured>,
    IProject<PaymentsOverview, PaymentFailed>,
    IProject<PaymentsOverview, PaymentRefunded>
{
    // Singleton - all events update the same document
    public string GetDocumentId(PaymentInitiated e) => "overview";
    public ProjectionResult<PaymentsOverview> Apply(PaymentsOverview state, PaymentInitiated e, ProjectionContext ctx)
    {
        state ??= new PaymentsOverview();
        state.TotalPayments++;
        return state;
    }

    public string GetDocumentId(PaymentAuthorized e) => "overview";
    public ProjectionResult<PaymentsOverview> Apply(PaymentsOverview state, PaymentAuthorized e, ProjectionContext ctx)
    {
        state ??= new PaymentsOverview();
        state.AuthorizedPayments++;
        return state;
    }

    public string GetDocumentId(PaymentCaptured e) => "overview";
    public ProjectionResult<PaymentsOverview> Apply(PaymentsOverview state, PaymentCaptured e, ProjectionContext ctx)
    {
        state ??= new PaymentsOverview();
        state.CapturedPayments++;
        state.TotalCapturedAmount += e.CapturedAmount;
        return state;
    }

    public string GetDocumentId(PaymentFailed e) => "overview";
    public ProjectionResult<PaymentsOverview> Apply(PaymentsOverview state, PaymentFailed e, ProjectionContext ctx)
    {
        state ??= new PaymentsOverview();
        state.FailedPayments++;
        return state;
    }

    public string GetDocumentId(PaymentRefunded e) => "overview";
    public ProjectionResult<PaymentsOverview> Apply(PaymentsOverview state, PaymentRefunded e, ProjectionContext ctx)
    {
        state ??= new PaymentsOverview();
        state.RefundedPayments++;
        state.TotalRefundedAmount += e.RefundedAmount;
        return state;
    }
}
