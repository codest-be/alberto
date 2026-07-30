using Microsoft.Extensions.DependencyInjection;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

/// <summary>
/// GraphQL queries for payments.
/// </summary>
public static class PaymentQueries
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
        // PaymentsOverviewProjection blends every tenant into one document, stored under
        // TenantScope.CrossTenant. Reading it with the request's tenant would look correct
        // and return nothing. The factory resolved here is the writer's own, so the only thing
        // this resolver decides is which tenant to read.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<PaymentsOverview>>>(
            $"{PaymentsModule.ModuleKey}:{nameof(PaymentsOverviewProjection)}");

        var states = await factory(TenantScope.CrossTenant).LoadManyAsync(
            ["overview"],
            ct: ct);

        return states.GetValueOrDefault("overview");
    }
}
