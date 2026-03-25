using Alberto.Dcb.Subscriptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.Endpoints;

/// <summary>
/// Admin endpoints for tenant lease management during blue-green deployments.
/// </summary>
internal static class TenantsEndpoints
{
    public static void MapTenantsEndpoints(
        this IEndpointRouteBuilder app,
        string basePath,
        string moduleKey,
        AdminOptions options)
    {
        var group = app.MapGroup($"{basePath}/{moduleKey}/api/tenants")
            .WithTags($"{moduleKey} Tenants");

        if (options.AuthorizationPolicy is not null)
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        // GET /tenants/leases - List all tenant leases
        group.MapGet("/leases", async (HttpContext ctx, CancellationToken ct) =>
        {
            var lockManager = ctx.RequestServices.GetKeyedService<ITenantProcessorLock>(moduleKey);
            if (lockManager is null)
            {
                return Results.Ok(new
                {
                    message = "Tenant distribution not configured",
                    leases = Array.Empty<TenantLeaseInfo>(),
                    thisReplicaId = (string?)null,
                    ownedByThisReplica = 0
                });
            }

            var consumer = ctx.RequestServices.GetKeyedService<PollingConsumer>(moduleKey);
            if (consumer is null)
            {
                return Results.Ok(new
                {
                    message = "Consumer not configured",
                    leases = Array.Empty<TenantLeaseInfo>(),
                    thisReplicaId = (string?)null,
                    ownedByThisReplica = 0
                });
            }

            var leases = await lockManager.GetAllLeasesAsync(consumer.ConsumerId, ct);
            return Results.Ok(new
            {
                leases,
                thisReplicaId = consumer.ReplicaId,
                ownedByThisReplica = consumer.OwnedTenantCount
            });
        }).WithName($"{moduleKey}_GetTenantLeases");

        if (!options.ReadOnly)
        {
            // POST /tenants/drain - Enter drain mode (stop renewing leases, wait for handoff)
            group.MapPost("/drain", async (HttpContext ctx, CancellationToken ct) =>
            {
                var consumer = ctx.RequestServices.GetKeyedService<PollingConsumer>(moduleKey);
                if (consumer is null)
                {
                    return Results.BadRequest(new { error = "Consumer not configured" });
                }

                await consumer.DrainAsync(ct);
                return Results.Ok(new { status = "drained", ownedTenants = consumer.OwnedTenantCount });
            }).WithName($"{moduleKey}_DrainTenants");

            // POST /tenants/reclaim - Clear cooldowns and claim available tenants
            group.MapPost("/reclaim", async (HttpContext ctx, CancellationToken ct) =>
            {
                var consumer = ctx.RequestServices.GetKeyedService<PollingConsumer>(moduleKey);
                if (consumer is null)
                {
                    return Results.BadRequest(new { error = "Consumer not configured" });
                }

                await consumer.ReclaimTenantsAsync(ct);
                return Results.Ok(new { status = "reclaimed", ownedTenants = consumer.OwnedTenantCount });
            }).WithName($"{moduleKey}_ReclaimTenants");

            // POST /tenants/release - Release all leases immediately
            group.MapPost("/release", async (HttpContext ctx, CancellationToken ct) =>
            {
                var consumer = ctx.RequestServices.GetKeyedService<PollingConsumer>(moduleKey);
                if (consumer is null)
                {
                    return Results.BadRequest(new { error = "Consumer not configured" });
                }

                await consumer.ReleaseAllTenantsAsync(ct);
                return Results.Ok(new { status = "released", ownedTenants = consumer.OwnedTenantCount });
            }).WithName($"{moduleKey}_ReleaseTenants");

            // POST /tenants/rebalance - Release excess tenants for fair distribution
            group.MapPost("/rebalance", async (HttpContext ctx, CancellationToken ct) =>
            {
                var consumer = ctx.RequestServices.GetKeyedService<PollingConsumer>(moduleKey);
                if (consumer is null)
                {
                    return Results.BadRequest(new { error = "Consumer not configured" });
                }

                var released = await consumer.RebalanceAsync(ct);
                return Results.Ok(new
                {
                    status = "rebalanced",
                    releasedCount = released,
                    ownedTenants = consumer.OwnedTenantCount
                });
            }).WithName($"{moduleKey}_RebalanceTenants");
        }
    }
}
