using Alberto.Payments.Infrastructure.ReadModels;
using CorePaymentStatus = Alberto.Payments.Core.Payment.PaymentStatus;

namespace Alberto.Orders.Api.GraphQL.Types;

/// <summary>
/// GraphQL type for Payment.
/// </summary>
public sealed record Payment(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod,
    CorePaymentStatus Status,
    string? AuthorizationCode,
    string? ErrorCode,
    string? ErrorMessage,
    decimal? RefundedAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? RefundedAt)
{
    public static Payment FromSummary(PaymentSummary s) => new(
        s.PaymentId,
        s.OrderId,
        s.Amount,
        s.Currency,
        s.PaymentMethod,
        (CorePaymentStatus)(int)s.Status,
        s.AuthorizationCode,
        s.ErrorCode,
        s.ErrorMessage,
        s.RefundedAmount,
        s.CreatedAt,
        s.AuthorizedAt,
        s.CapturedAt,
        s.RefundedAt);
}

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
