using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

/// <summary>
/// GraphQL type for Payment.
/// </summary>
/// <remarks>
/// Declared by this slice because this slice's projection is what fills it. <c>payment</c> also
/// returns it — a shared <em>output contract</em> between two read slices, which is a different
/// thing from a shared state record: nothing folds events into a <see cref="Payment"/>.
/// </remarks>
public sealed record Payment(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod,
    PaymentStatus Status,
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
        ToContractStatus(s.Status),
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
    /// These are two independent enums, and their ordinals do not line up: the contract one
    /// starts at <c>None = 0</c>, the read model's at <c>Initiated = 0</c>. The numeric cast this
    /// replaces therefore shifted every payment one rung down the ladder — an initiated payment
    /// reported <c>NONE</c>, a captured one <c>AUTHORIZED</c>, a refunded one <c>FAILED</c>. It
    /// went unseen because <c>recentPayments</c> returned nothing at all until the projection's
    /// store was scoped to the right tenant; this is the first status the field has ever
    /// actually served.
    /// <para>
    /// They used to share the name <c>PaymentStatus</c> too, which made the mistake easy to
    /// write and hard to see. The read model's is now <see cref="PaymentSummaryStatus"/>, so the
    /// two can never be swapped by a using-directive. Naming both sides here makes a future
    /// member added to either enum a compile error rather than another silent shift.
    /// </para>
    /// </remarks>
    private static PaymentStatus ToContractStatus(PaymentSummaryStatus status) => status switch
    {
        PaymentSummaryStatus.Initiated => PaymentStatus.Initiated,
        PaymentSummaryStatus.Authorized => PaymentStatus.Authorized,
        PaymentSummaryStatus.Captured => PaymentStatus.Captured,
        PaymentSummaryStatus.Failed => PaymentStatus.Failed,
        PaymentSummaryStatus.Refunded => PaymentStatus.Refunded,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unmapped payment status from the read model."),
    };
}

public static class PaymentSummariesQuery
{
    /// <summary>
    /// Gets recent payments from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets the calling tenant's recent payments, ordered by last update.")]
    public static async Task<IReadOnlyList<Payment>> GetRecentPayments(
        [Service] ITenantAccessor tenantAccessor,
        [Service] IServiceProvider sp,
        int limit = 20,
        CancellationToken ct = default)
    {
        // One document per PaymentId, so the documents belong to individual tenants and this field
        // must be read under one. The factory resolved here is the writer's own, so the only thing
        // this resolver decides is which tenant to read — and that is where the filtering happens:
        // a tenant-enabled module's projection rows are keyed by tenant, so there is no unscoped
        // read to accidentally fall back to.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<PaymentSummary>>>(
            $"{PaymentsModule.ModuleKey}:{nameof(PaymentSummaryProjection)}");

        var summaries = await factory(tenantAccessor.TenantId).ListRecentAsync(limit, ct);
        return summaries.Select(Payment.FromSummary).ToList();
    }
}
