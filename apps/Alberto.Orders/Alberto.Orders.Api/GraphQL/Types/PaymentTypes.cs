using Alberto.Payments.Platform;
using CorePaymentStatus = Alberto.Payments.Contracts.PaymentStatus;

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
        ToCoreStatus(s.Status),
        s.AuthorizationCode,
        s.ErrorCode,
        s.ErrorMessage,
        s.RefundedAmount,
        s.CreatedAt,
        s.AuthorizedAt,
        s.CapturedAt,
        s.RefundedAt);

    /// <summary>
    /// Maps the read model's status onto the one this GraphQL type exposes.
    /// </summary>
    /// <remarks>
    /// These are two independent enums that happen to share member names, and their ordinals do
    /// not line up: the core one starts at <c>None = 0</c>, the read model's at
    /// <c>Initiated = 0</c>. The numeric cast this replaces therefore shifted every payment one
    /// rung down the ladder — an initiated payment reported <c>NONE</c>, a captured one
    /// <c>AUTHORIZED</c>, a refunded one <c>FAILED</c>. It went unseen because
    /// <c>recentPayments</c> returned nothing at all until the projection's store was scoped to
    /// the right tenant; this is the first status the field has ever actually served.
    /// <para>
    /// Naming both sides makes a future member added to either enum a compile error here rather
    /// than another silent shift.
    /// </para>
    /// </remarks>
    private static CorePaymentStatus ToCoreStatus(PaymentStatus status) => status switch
    {
        PaymentStatus.Initiated => CorePaymentStatus.Initiated,
        PaymentStatus.Authorized => CorePaymentStatus.Authorized,
        PaymentStatus.Captured => CorePaymentStatus.Captured,
        PaymentStatus.Failed => CorePaymentStatus.Failed,
        PaymentStatus.Refunded => CorePaymentStatus.Refunded,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unmapped payment status from the read model."),
    };
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
