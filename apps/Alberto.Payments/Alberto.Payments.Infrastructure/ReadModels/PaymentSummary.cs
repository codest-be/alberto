namespace Alberto.Payments.Infrastructure.ReadModels;

/// <summary>
/// Read model for a single payment.
/// </summary>
public sealed record PaymentSummary
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public PaymentStatus Status { get; init; }
    public string? AuthorizationCode { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public decimal? RefundedAmount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? AuthorizedAt { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public DateTimeOffset? RefundedAt { get; init; }
}

public enum PaymentStatus
{
    Initiated,
    Authorized,
    Captured,
    Failed,
    Refunded
}
