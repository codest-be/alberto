using Alberto.Dcb.Admin.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.Endpoints;

internal static class ProjectionStatesEndpoints
{
    public static void MapProjectionStatesEndpoints(
        this IEndpointRouteBuilder app,
        string basePath,
        string moduleKey,
        AdminOptions options)
    {
        var group = app.MapGroup($"{basePath}/{moduleKey}/api/projection-states")
            .WithTags($"{moduleKey} Projection States");

        if (options.AuthorizationPolicy is not null)
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapGet("/types", async (HttpContext ctx, CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var types = await service.GetProjectionTypesAsync(ct);
            return Results.Ok(types);
        }).WithName($"{moduleKey}_GetProjectionTypes");

        group.MapGet("/{projectionType}", async (
            string projectionType,
            string? tenantId,
            int page,
            int pageSize,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var result = await service.GetProjectionStatesAsync(
                projectionType,
                tenantId,
                page > 0 ? page : 1,
                pageSize > 0 ? pageSize : 50,
                ct);
            return Results.Ok(result);
        }).WithName($"{moduleKey}_GetProjectionStates");

        group.MapGet("/{projectionType}/{documentId}", async (
            string projectionType,
            string documentId,
            string? tenantId,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var state = await service.GetProjectionStateAsync(projectionType, documentId, tenantId, ct);
            return state is null ? Results.NotFound() : Results.Ok(state);
        }).WithName($"{moduleKey}_GetProjectionState");
    }
}
