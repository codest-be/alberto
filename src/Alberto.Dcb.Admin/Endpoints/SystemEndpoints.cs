using Alberto.Dcb.Admin.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            try
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                var info = await service.GetSystemInfoAsync(ct);
                return Results.Ok(info);
            }
            catch (Exception ex)
            {
                var env = ctx.RequestServices.GetService<IHostEnvironment>();
                if (env?.IsDevelopment() == true)
                {
                    return Results.Problem(
                        detail: $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                        title: "Error loading system info");
                }
                throw;
            }
        }).WithName($"{moduleKey}_GetSystemInfo");

        group.MapGet("/position", async (HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                var position = await service.GetLastGlobalPositionAsync(ct);
                return Results.Ok(new { position });
            }
            catch (Exception ex)
            {
                var env = ctx.RequestServices.GetService<IHostEnvironment>();
                if (env?.IsDevelopment() == true)
                {
                    return Results.Problem(
                        detail: $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                        title: "Error loading global position");
                }
                throw;
            }
        }).WithName($"{moduleKey}_GetGlobalPosition");
    }
}
