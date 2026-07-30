using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Payments.Features;

/// <summary>
/// Payment totals across the whole system, folded into a single document.
/// </summary>
/// <remarks>
/// This is a <strong>cross-tenant</strong> aggregate. The async control loop consumes events for
/// every tenant through one singleton state store, and the document-ID selectors below never see
/// the tenant, so there is no seam at which a per-tenant key could be derived. Readers must build
/// their store the same tenant-agnostic way; a tenant-scoped store queries a different primary key
/// and would silently return nothing. That is why <see cref="StateStore"/> lives here rather than
/// in the module registration: the writer and
/// <see cref="PaymentsOverviewQuery.GetPaymentsOverview"/> now share one definition of the store.
///
/// For a genuinely tenant-isolated read model, see <see cref="PaymentSummaryProjection"/>, which
/// keys one document per payment and is read under the request's own tenant.
/// </remarks>
public static class PaymentsOverviewProjection
{
    /// <summary>
    /// The single document this projection maintains: it is one row, not one per payment.
    /// </summary>
    public const string DocumentId = "overview";

    /// <summary>
    /// Builds the state store this projection writes and <c>paymentsOverview</c> reads, so the two
    /// cannot disagree about schema, projection name, rebuild version or tenancy.
    /// </summary>
    public static Func<string?, IStateStore<PaymentsOverview>> StateStore(ProjectionStoreContext ctx)
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsModule.ModuleKey);

        return tenantId => new PostgresStateStore<PaymentsOverview>(
            dataSource,
            nameof(PaymentsOverviewProjection),
            "payments",
            rebuildVersion: ctx.RebuildVersion,
            tenantId: TenantScope.CrossTenantFor(tenantId));
    }

    public static readonly ProjectionDeclaration<PaymentsOverview> Declaration =
        DeclareProjection.For<PaymentsOverview>(nameof(PaymentsOverviewProjection))
            .On<PaymentInitiated>(
                id: _ => DocumentId,
                apply: (state, _, _) => state with { TotalPayments = state.TotalPayments + 1 })
            .On<PaymentAuthorized>(
                id: _ => DocumentId,
                apply: (state, _, _) => state with { AuthorizedPayments = state.AuthorizedPayments + 1 })
            .On<PaymentCaptured>(
                id: _ => DocumentId,
                apply: (state, e, _) => Apply(state, e))
            .On<PaymentFailed>(
                id: _ => DocumentId,
                apply: (state, _, _) => state with { FailedPayments = state.FailedPayments + 1 })
            .On<PaymentRefunded>(
                id: _ => DocumentId,
                apply: (state, e, _) => Apply(state, e))
            .Build();

    private static PaymentsOverview Apply(PaymentsOverview state, PaymentCaptured e)
        => state with
        {
            CapturedPayments = state.CapturedPayments + 1,
            TotalCapturedAmount = state.TotalCapturedAmount + e.CapturedAmount
        };

    private static PaymentsOverview Apply(PaymentsOverview state, PaymentRefunded e)
        => state with
        {
            RefundedPayments = state.RefundedPayments + 1,
            TotalRefundedAmount = state.TotalRefundedAmount + e.RefundedAmount
        };
}
