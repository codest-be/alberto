using Alberto.Dcb.Admin.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Alberto.Dcb.Admin.Endpoints;

internal static class ProcessorsEndpoints
{
    public static void MapProcessorsEndpoints(
        this IEndpointRouteBuilder app,
        string basePath,
        string moduleKey,
        AdminOptions options)
    {
        var group = app.MapGroup($"{basePath}/{moduleKey}/api/processors")
            .WithTags($"{moduleKey} Processors");

        if (options.AuthorizationPolicy is not null)
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapGet("/", async (HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                var processors = await service.GetProcessorsAsync(ct);
                return Results.Ok(processors);
            }
            catch (Exception ex)
            {
                var env = ctx.RequestServices.GetService<IHostEnvironment>();
                if (env?.IsDevelopment() == true)
                {
                    return Results.Problem(
                        detail: $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                        title: "Error loading processors");
                }
                throw;
            }
        }).WithName($"{moduleKey}_GetProcessors");

        if (!options.ReadOnly)
        {
            group.MapPost("/{processorId}/activate", async (string processorId, HttpContext ctx, CancellationToken ct) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                await service.SetProcessorActiveAsync(processorId, true, ct);
                return Results.NoContent();
            }).WithName($"{moduleKey}_ActivateProcessor");

            group.MapPost("/{processorId}/deactivate", async (string processorId, HttpContext ctx, CancellationToken ct) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                await service.SetProcessorActiveAsync(processorId, false, ct);
                return Results.NoContent();
            }).WithName($"{moduleKey}_DeactivateProcessor");

            group.MapPost("/{processorId}/rebuild", async (string processorId, HttpContext ctx, bool clearState = true, CancellationToken ct = default) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                var result = await service.StartRebuildAsync(processorId, clearState, ct);
                return Results.Ok(result);
            }).WithName($"{moduleKey}_StartRebuild");

            group.MapGet("/{processorId}/rebuild/status", async (string processorId, HttpContext ctx, CancellationToken ct) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                var result = await service.GetRebuildStatusAsync(processorId, ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            }).WithName($"{moduleKey}_GetRebuildStatus");

            group.MapPost("/{processorId}/rebuild/cancel", async (string processorId, HttpContext ctx, CancellationToken ct) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                await service.CancelRebuildAsync(processorId, ct);
                return Results.NoContent();
            }).WithName($"{moduleKey}_CancelRebuild");
        }
    }
}
