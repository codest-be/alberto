using Alberto.Dcb;

namespace Alberto.Payments.Core.Events;

/// <summary>
/// Payment was initiated.
/// </summary>
[EventType("payment-initiated")]
public sealed record PaymentInitiated(
    [property: Tag(Tags.Payment)] Guid PaymentId,
    [property: Tag(Tags.Order)] Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod) : IEvent;

/// <summary>
/// Payment was authorized by the payment provider.
/// </summary>
[EventType("payment-authorized")]
public sealed record PaymentAuthorized(
    [property: Tag(Tags.Payment)] Guid PaymentId,
    string AuthorizationCode,
    DateTimeOffset AuthorizedAt) : IEvent;

/// <summary>
/// Payment was captured (funds transferred).
/// </summary>
[EventType("payment-captured")]
public sealed record PaymentCaptured(
    [property: Tag(Tags.Payment)] Guid PaymentId,
    decimal CapturedAmount,
    DateTimeOffset CapturedAt) : IEvent;

/// <summary>
/// Payment failed.
/// </summary>
[EventType("payment-failed")]
public sealed record PaymentFailed(
    [property: Tag(Tags.Payment)] Guid PaymentId,
    string ErrorCode,
    string ErrorMessage) : IEvent;

/// <summary>
/// Payment was refunded.
/// </summary>
[EventType("payment-refunded")]
public sealed record PaymentRefunded(
    [property: Tag(Tags.Payment)] Guid PaymentId,
    decimal RefundedAmount,
    string Reason,
    DateTimeOffset RefundedAt) : IEvent;
