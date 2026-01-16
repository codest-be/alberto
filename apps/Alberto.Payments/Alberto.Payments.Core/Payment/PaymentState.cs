using Alberto.Payments.Core.Payment.Actions;

namespace Alberto.Payments.Core.Payment;

/// <summary>
/// The current state of a payment, rebuilt from events.
/// </summary>
public sealed record PaymentState :
    IInitiatePaymentState,
    IAuthorizePaymentState,
    ICapturePaymentState,
    IFailPaymentState,
    IRefundPaymentState
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public PaymentStatus Status { get; init; } = PaymentStatus.None;
    public string? AuthorizationCode { get; init; }
    public decimal? CapturedAmount { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public decimal? RefundedAmount { get; init; }
    public string? RefundReason { get; init; }
    public DateTimeOffset? AuthorizedAt { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public DateTimeOffset? RefundedAt { get; init; }

    // Computed properties
    public bool Exists => PaymentId != Guid.Empty;
    public bool IsInitiated => Status == PaymentStatus.Initiated;
    public bool IsAuthorized => Status == PaymentStatus.Authorized;
    public bool IsCaptured => Status == PaymentStatus.Captured;
    public bool IsFailed => Status == PaymentStatus.Failed;
    public bool IsRefunded => Status == PaymentStatus.Refunded;
    public bool CanBeAuthorized => IsInitiated;
    public bool CanBeCaptured => IsAuthorized;
    public bool CanBeFailed => IsInitiated || IsAuthorized;
    public bool CanBeRefunded => IsCaptured;
}

/// <summary>
/// Possible states of a payment.
/// </summary>
public enum PaymentStatus
{
    None = 0,
    Initiated = 1,
    Authorized = 2,
    Captured = 3,
    Failed = 4,
    Refunded = 5
}
