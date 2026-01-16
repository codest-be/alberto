namespace Alberto.Payments.Infrastructure.ReadModels;

/// <summary>
/// Overview/aggregate read model for payments.
/// </summary>
public sealed class PaymentsOverview
{
    public int TotalPayments { get; set; }
    public int AuthorizedPayments { get; set; }
    public int CapturedPayments { get; set; }
    public int FailedPayments { get; set; }
    public int RefundedPayments { get; set; }
    public decimal TotalCapturedAmount { get; set; }
    public decimal TotalRefundedAmount { get; set; }
}
