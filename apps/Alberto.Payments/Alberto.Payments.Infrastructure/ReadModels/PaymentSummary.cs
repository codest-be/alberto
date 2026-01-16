namespace Alberto.Payments.Infrastructure.ReadModels;

/// <summary>
/// Read model for a single payment.
/// </summary>
public sealed class PaymentSummary
{
    public Guid PaymentId { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public string? AuthorizationCode { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public decimal? RefundedAmount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
    public DateTimeOffset? RefundedAt { get; set; }
}

public enum PaymentStatus
{
    Initiated,
    Authorized,
    Captured,
    Failed,
    Refunded
}
