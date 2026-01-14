using Alberto.Dcb.Admin.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.Endpoints;

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(
        this IEndpointRouteBuilder app,
        string basePath,
        string moduleKey,
        AdminOptions options)
    {
        var group = app.MapGroup($"{basePath}/{moduleKey}/api/system")
            .WithTags($"{moduleKey} System");

        if (options.AuthorizationPolicy is not null)
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapGet("/info", async (HttpContext ctx, CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var info = await service.GetSystemInfoAsync(ct);
            return Results.Ok(info);
        }).WithName($"{moduleKey}_GetSystemInfo");

        group.MapGet("/position", async (HttpContext ctx, CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var position = await service.GetLastGlobalPositionAsync(ct);
            return Results.Ok(new { position });
        }).WithName($"{moduleKey}_GetGlobalPosition");
    }
}
