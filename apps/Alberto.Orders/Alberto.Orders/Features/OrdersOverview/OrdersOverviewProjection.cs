using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Orders.Features;

/// <summary>
/// Running counters over all order activity, kept in a single JSONB document.
/// </summary>
/// <remarks>
/// This is a <strong>cross-tenant</strong> aggregate. The async control loop consumes events for
/// every tenant through one singleton state store, and <c>On&lt;TEvent&gt;</c> hands the document-ID
/// selector only the parsed event — never the tenant — so there is no seam at which a per-tenant
/// key could be derived. Readers must construct their store the same tenant-agnostic way; a
/// tenant-scoped store queries a different primary key and would silently return nothing. That is
/// why <see cref="StateStore"/> lives here rather than in the module registration: the writer and
/// <see cref="OrdersOverviewQuery.GetOrdersOverview"/> now share one definition of the store.
///
/// For a genuinely tenant-isolated read model, see <see cref="OrderSummaryEfProjection"/>, which
/// stores the tenant as a column and lets the query filter on it.
/// </remarks>
public static class OrdersOverviewProjection
{
    /// <summary>
    /// The single document this projection maintains.
    /// </summary>
    public const string DocumentId = "overview";

    /// <summary>
    /// Builds the state store this projection writes and <c>ordersOverview</c> reads, so the two
    /// cannot disagree about schema, projection name, rebuild version or tenancy.
    /// </summary>
    public static Func<string?, IStateStore<OrdersOverview>> StateStore(ProjectionStoreContext ctx)
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(OrdersModule.ModuleKey);

        return tenantId => new PostgresStateStore<OrdersOverview>(
            dataSource,
            nameof(OrdersOverviewProjection),
            "orders",
            rebuildVersion: ctx.RebuildVersion,
            tenantId: TenantScope.CrossTenantFor(tenantId));
    }

    public static readonly ProjectionDeclaration<OrdersOverview> Declaration =
        DeclareProjection.For<OrdersOverview>(nameof(OrdersOverviewProjection))
            .On<OrderCreated>(
                id: _ => DocumentId,
                apply: (state, e, ctx) => ApplyOrderCreated(state, e, ctx))
            .On<OrderConfirmed>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { DraftOrders = Math.Max(0, state.DraftOrders - 1), ConfirmedOrders = state.ConfirmedOrders + 1, UpdatedAt = ctx.Timestamp })
            .On<OrderShipped>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { ConfirmedOrders = Math.Max(0, state.ConfirmedOrders - 1), ShippedOrders = state.ShippedOrders + 1, UpdatedAt = ctx.Timestamp })
            .On<OrderDelivered>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { ShippedOrders = Math.Max(0, state.ShippedOrders - 1), DeliveredOrders = state.DeliveredOrders + 1, UpdatedAt = ctx.Timestamp })
            .On<OrderCancelled>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { DraftOrders = Math.Max(0, state.DraftOrders - 1), CancelledOrders = state.CancelledOrders + 1, UpdatedAt = ctx.Timestamp })
            .Build();

    private static OrdersOverview ApplyOrderCreated(OrdersOverview state, OrderCreated e, ProjectionContext ctx)
    {
        var orderTotal = e.LineItems.Sum(x => x.Total);
        var newTotalOrders = state.TotalOrders + 1;
        var newTotalRevenue = state.TotalRevenue + orderTotal;
        return state with
        {
            TotalOrders = newTotalOrders,
            DraftOrders = state.DraftOrders + 1,
            TotalRevenue = newTotalRevenue,
            AverageOrderValue = newTotalOrders > 0 ? newTotalRevenue / newTotalOrders : 0,
            LastOrderAt = ctx.Timestamp,
            UpdatedAt = ctx.Timestamp
        };
    }
}
