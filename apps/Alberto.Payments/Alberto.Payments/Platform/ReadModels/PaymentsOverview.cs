namespace Alberto.Payments.Platform;

/// <summary>
/// Overview/aggregate read model for payments.
/// </summary>
public sealed record PaymentsOverview
{
    public int TotalPayments { get; init; }
    public int AuthorizedPayments { get; init; }
    public int CapturedPayments { get; init; }
    public int FailedPayments { get; init; }
    public int RefundedPayments { get; init; }
    public decimal TotalCapturedAmount { get; init; }
    public decimal TotalRefundedAmount { get; init; }
}
