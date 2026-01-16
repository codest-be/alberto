using Alberto.Dcb;

namespace Alberto.Payments.Core.Events;

/// <summary>
/// Payment was initiated.
/// </summary>
[EventType("payment-initiated")]
public sealed record PaymentInitiated(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod) : IEvent;

/// <summary>
/// Payment was authorized by the payment provider.
/// </summary>
[EventType("payment-authorized")]
public sealed record PaymentAuthorized(
    Guid PaymentId,
    string AuthorizationCode,
    DateTimeOffset AuthorizedAt) : IEvent;

/// <summary>
/// Payment was captured (funds transferred).
/// </summary>
[EventType("payment-captured")]
public sealed record PaymentCaptured(
    Guid PaymentId,
    decimal CapturedAmount,
    DateTimeOffset CapturedAt) : IEvent;

/// <summary>
/// Payment failed.
/// </summary>
[EventType("payment-failed")]
public sealed record PaymentFailed(
    Guid PaymentId,
    string ErrorCode,
    string ErrorMessage) : IEvent;

/// <summary>
/// Payment was refunded.
/// </summary>
[EventType("payment-refunded")]
public sealed record PaymentRefunded(
    Guid PaymentId,
    decimal RefundedAmount,
    string Reason,
    DateTimeOffset RefundedAt) : IEvent;
