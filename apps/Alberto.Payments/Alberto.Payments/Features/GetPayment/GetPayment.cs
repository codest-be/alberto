using Alberto.Dcb;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

/// <summary>
/// What <c>getPayment</c> shows. This is the widest state in the module because the field exposes
/// the whole payment — but it is still one slice's state, folded by one slice's evolver, and no
/// decision depends on it.
/// </summary>
public sealed record GetPaymentState
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public PaymentStatus Status { get; init; } = PaymentStatus.None;
    public string? AuthorizationCode { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public decimal? RefundedAmount { get; init; }
    public DateTimeOffset? AuthorizedAt { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public DateTimeOffset? RefundedAt { get; init; }

    public bool Exists => PaymentId != Guid.Empty;
}

/// <summary>
/// Folds the payment's whole history for the read field.
/// </summary>
/// <remarks>
/// This replaces a hand-rolled loop that switched over event-type ids, deserialized each payload
/// itself and then switched a second time to pick an <c>Apply</c> overload. The two switches had to
/// agree with each other and with the event registry, and a new payment event would have been
/// silently dropped by whichever of them was not updated. The evolver derives all three from the
/// <c>IEvolve&lt;,&gt;</c> interfaces below.
/// </remarks>
public sealed class GetPaymentEvolver : Evolver<GetPaymentState>,
    IEvolve<GetPaymentState, PaymentInitiated>,
    IEvolve<GetPaymentState, PaymentAuthorized>,
    IEvolve<GetPaymentState, PaymentCaptured>,
    IEvolve<GetPaymentState, PaymentFailed>,
    IEvolve<GetPaymentState, PaymentRefunded>
{
    public GetPaymentState Apply(GetPaymentState s, PaymentInitiated e) => s with
    {
        PaymentId = e.PaymentId,
        OrderId = e.OrderId,
        Amount = e.Amount,
        Currency = e.Currency,
        PaymentMethod = e.PaymentMethod,
        Status = PaymentStatus.Initiated
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentAuthorized e) => s with
    {
        Status = PaymentStatus.Authorized,
        AuthorizationCode = e.AuthorizationCode,
        AuthorizedAt = e.AuthorizedAt
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentCaptured e) => s with
    {
        Status = PaymentStatus.Captured,
        CapturedAt = e.CapturedAt
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentFailed e) => s with
    {
        Status = PaymentStatus.Failed,
        ErrorCode = e.ErrorCode,
        ErrorMessage = e.ErrorMessage
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentRefunded e) => s with
    {
        Status = PaymentStatus.Refunded,
        RefundedAmount = e.RefundedAmount,
        RefundedAt = e.RefundedAt
    };
}

public static class GetPaymentQuery
{
    private static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    /// <summary>
    /// Gets a payment by ID from the event store (real-time, consistent).
    /// </summary>
    [Query]
    [GraphQLDescription("Gets a payment by ID, rebuilt from events for consistency.")]
    public static async Task<Payment?> GetPayment(
        Guid paymentId,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(PaymentsModule.ModuleKey);
        var events = await backend.StreamAsync(Boundary(paymentId), cancellationToken: ct);
        var state = new GetPaymentEvolver().Reconstitute(events);

        return state.Exists ? ToGraphQL(state) : null;
    }

    private static Payment ToGraphQL(GetPaymentState state) => new(
        state.PaymentId,
        state.OrderId,
        state.Amount,
        state.Currency,
        state.PaymentMethod,
        state.Status,
        state.AuthorizationCode,
        state.ErrorCode,
        state.ErrorMessage,
        state.RefundedAmount,
        DateTimeOffset.MinValue, // Would need to track CreatedAt in state
        state.AuthorizedAt,
        state.CapturedAt,
        state.RefundedAt);
}
