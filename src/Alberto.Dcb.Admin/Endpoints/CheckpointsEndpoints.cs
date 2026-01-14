using Alberto.Dcb.Admin.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.Endpoints;

internal static class CheckpointsEndpoints
{
    public static void MapCheckpointsEndpoints(
        this IEndpointRouteBuilder app,
        string basePath,
        string moduleKey,
        AdminOptions options)
    {
        var group = app.MapGroup($"{basePath}/{moduleKey}/api/checkpoints")
            .WithTags($"{moduleKey} Checkpoints");

        if (options.AuthorizationPolicy is not null)
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapGet("/", async (HttpContext ctx, CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var checkpoints = await service.GetCheckpointsAsync(ct);
            return Results.Ok(checkpoints);
        }).WithName($"{moduleKey}_GetCheckpoints");

        if (!options.ReadOnly)
        {
            group.MapDelete("/{processorId}", async (string processorId, HttpContext ctx, CancellationToken ct) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                await service.ResetCheckpointAsync(processorId, ct);
                return Results.NoContent();
            }).WithName($"{moduleKey}_ResetCheckpoint");

            group.MapPut("/{processorId}", async (string processorId, SetCheckpointRequest request, HttpContext ctx, CancellationToken ct) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                await service.SetCheckpointAsync(processorId, request.Position, ct);
                return Results.NoContent();
            }).WithName($"{moduleKey}_SetCheckpoint");
        }
    }

    public record SetCheckpointRequest(long Position);
}
