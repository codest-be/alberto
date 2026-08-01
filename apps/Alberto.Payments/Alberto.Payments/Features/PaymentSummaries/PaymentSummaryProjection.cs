using Alberto;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Payments.Features;

/// <summary>
/// One row per payment, tracking where it got to in the authorize/capture/refund lifecycle.
/// </summary>
/// <remarks>
/// Unlike <see cref="PaymentsOverviewProjection"/> this is not an aggregate: it keys one document
/// per <c>PaymentId</c>, so every document belongs to a single tenant and both the writer and
/// <see cref="PaymentSummariesQuery.GetRecentPayments"/> read it under one. <see cref="StateStore"/>
/// lives here so the two cannot disagree about that — a store built tenant-agnostically queries a
/// different primary key and returns nothing.
/// </remarks>
public static class PaymentSummaryProjection
{
    /// <summary>
    /// Builds the state store this projection writes and <c>recentPayments</c> reads, so the two
    /// cannot disagree about schema, projection name, rebuild version or tenancy.
    /// </summary>
    public static Func<string?, IStateStore<PaymentSummary>> StateStore(ProjectionStoreContext ctx)
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsModule.ModuleKey);

        return tenantId => new PostgresStateStore<PaymentSummary>(
            dataSource,
            nameof(PaymentSummaryProjection),
            "payments",
            rebuildVersion: ctx.RebuildVersion,
            tenantId: tenantId);
    }

    public static readonly ProjectionDeclaration<PaymentSummary> Declaration =
        DeclareProjection.For<PaymentSummary>(nameof(PaymentSummaryProjection))
            .On<PaymentInitiated>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, ctx) => Apply(state, e, ctx))
            .On<PaymentAuthorized>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentCaptured>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentFailed>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentRefunded>(
                id: e => e.PaymentId.ToString(),
                apply: (state, e, _) => Apply(state, e))
            .Build();

    private static PaymentSummary Apply(PaymentSummary state, PaymentInitiated e, ProjectionContext ctx)
        => new PaymentSummary
        {
            PaymentId = e.PaymentId,
            OrderId = e.OrderId,
            Amount = e.Amount,
            Currency = e.Currency,
            PaymentMethod = e.PaymentMethod,
            Status = PaymentSummaryStatus.Initiated,
            CreatedAt = ctx.Timestamp
        };

    private static PaymentSummary Apply(PaymentSummary state, PaymentAuthorized e)
        => state with
        {
            Status = PaymentSummaryStatus.Authorized,
            AuthorizationCode = e.AuthorizationCode,
            AuthorizedAt = e.AuthorizedAt
        };

    private static PaymentSummary Apply(PaymentSummary state, PaymentCaptured e)
        => state with { Status = PaymentSummaryStatus.Captured, CapturedAt = e.CapturedAt };

    private static PaymentSummary Apply(PaymentSummary state, PaymentFailed e)
        => state with
        {
            Status = PaymentSummaryStatus.Failed,
            ErrorCode = e.ErrorCode,
            ErrorMessage = e.ErrorMessage
        };

    private static PaymentSummary Apply(PaymentSummary state, PaymentRefunded e)
        => state with
        {
            Status = PaymentSummaryStatus.Refunded,
            RefundedAmount = e.RefundedAmount,
            RefundedAt = e.RefundedAt
        };
}
