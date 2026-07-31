using Alberto.Subscriptions;
using Alberto.Tenancy;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

public static class OrdersOverviewQuery
{
    /// <summary>
    /// Gets the orders overview statistics from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets aggregated order statistics from the async projection.")]
    public static async Task<OrdersOverview?> GetOrdersOverview(
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        // A cross-tenant aggregate: the control loop blends every tenant's events into one
        // document under TenantScope.CrossTenant. The factory resolved here is the writer's own,
        // so the only thing this resolver decides is which tenant to read — and passing the
        // request's tenant would be wrong, not merely empty.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<OrdersOverview>>>(
            $"{OrdersModule.ModuleKey}:{nameof(OrdersOverviewProjection)}");

        var states = await factory(TenantScope.CrossTenant)
            .LoadManyAsync([OrdersOverviewProjection.DocumentId], ct: ct);

        return states.GetValueOrDefault(OrdersOverviewProjection.DocumentId);
    }
}
