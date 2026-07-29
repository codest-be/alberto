namespace Alberto.Payments.Contracts;

// Staging: Tasks 13–17 move each of these into the slice that is the only thing that names it.

/// <summary>
/// Input for initiating a payment.
/// </summary>
public sealed record InitiatePaymentInput(
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod);

/// <summary>
/// Input for capturing a payment.
/// </summary>
public sealed record CapturePaymentInput(
    Guid PaymentId,
    decimal? Amount);

/// <summary>
/// Input for failing a payment.
/// </summary>
public sealed record FailPaymentInput(
    Guid PaymentId,
    string ErrorCode,
    string ErrorMessage);

/// <summary>
/// Input for refunding a payment.
/// </summary>
public sealed record RefundPaymentInput(
    Guid PaymentId,
    decimal Amount,
    string Reason);

/// <summary>
/// Result of initiating a payment.
/// </summary>
public readonly record struct InitiatePaymentResult(Guid PaymentId);
