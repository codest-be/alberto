using Alberto.Subscriptions;
using Alberto.Tenancy;
using Alberto.Payments.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Payments.Features;

public static class PaymentsOverviewQuery
{
    /// <summary>
    /// Gets the payments overview statistics from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets aggregated payment statistics from the async projection.")]
    public static async Task<PaymentsOverview?> GetPaymentsOverview(
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        // A cross-tenant aggregate: the control loop blends every tenant's events into one
        // document under TenantScope.CrossTenant. The factory resolved here is the writer's own,
        // so the only thing this resolver decides is which tenant to read — and passing the
        // request's tenant would be wrong, not merely empty.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<PaymentsOverview>>>(
            $"{PaymentsModule.ModuleKey}:{nameof(PaymentsOverviewProjection)}");

        var states = await factory(TenantScope.CrossTenant)
            .LoadManyAsync([PaymentsOverviewProjection.DocumentId], ct: ct);

        return states.GetValueOrDefault(PaymentsOverviewProjection.DocumentId);
    }
}
