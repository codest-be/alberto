using Alberto.Dcb;
using Microsoft.Extensions.DependencyInjection;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// GraphQL queries for orders.
/// </summary>
/// <remarks>
/// Staging: what is left here moves into <c>Features/OrdersOverview</c>, after which this file
/// goes.
/// </remarks>
public static class OrderQueries
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
        // OrdersOverviewProjection is a cross-tenant aggregate: the control loop accumulates
        // events from every tenant into a single document, stored under TenantScope.CrossTenant
        // because a tenant-enabled module's projection rows are keyed by tenant and this one
        // belongs to no single tenant. The factory resolved here is the writer's own, so the
        // only thing this resolver decides is which tenant to read — and passing the request's
        // tenant here would be wrong, not merely empty.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<OrdersOverview>>>(
            $"{OrdersModule.ModuleKey}:{nameof(OrdersOverviewProjection)}");
        var stateStore = factory(TenantScope.CrossTenant);

        var states = await stateStore.LoadManyAsync(
            [OrdersOverviewProjection.DocumentId],
            ct: ct);

        return states.GetValueOrDefault(OrdersOverviewProjection.DocumentId);
    }
}
