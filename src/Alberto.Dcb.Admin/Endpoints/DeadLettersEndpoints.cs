using Alberto.Dcb.Admin.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.Endpoints;

internal static class DeadLettersEndpoints
{
    public static void MapDeadLettersEndpoints(
        this IEndpointRouteBuilder app,
        string basePath,
        string moduleKey,
        AdminOptions options)
    {
        var group = app.MapGroup($"{basePath}/{moduleKey}/api/dead-letters")
            .WithTags($"{moduleKey} Dead Letters");

        if (options.AuthorizationPolicy is not null)
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapGet("/", async (
            string? processorId,
            int page,
            int pageSize,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var result = await service.GetDeadLettersAsync(processorId, page > 0 ? page : 1, pageSize > 0 ? pageSize : 50, ct);
            return Results.Ok(result);
        }).WithName($"{moduleKey}_GetDeadLetters");

        group.MapGet("/count", async (string? processorId, HttpContext ctx, CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var count = await service.GetDeadLetterCountAsync(processorId, ct);
            return Results.Ok(new { count });
        }).WithName($"{moduleKey}_GetDeadLetterCount");

        group.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
            var deadLetter = await service.GetDeadLetterAsync(id, ct);
            return deadLetter is null ? Results.NotFound() : Results.Ok(deadLetter);
        }).WithName($"{moduleKey}_GetDeadLetter");

        if (!options.ReadOnly)
        {
            group.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, CancellationToken ct) =>
            {
                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                await service.RemoveDeadLetterAsync(id, ct);
                return Results.NoContent();
            }).WithName($"{moduleKey}_RemoveDeadLetter");

            group.MapDelete("/", async (string processorId, HttpContext ctx, CancellationToken ct) =>
            {
                if (string.IsNullOrEmpty(processorId))
                    return Results.BadRequest("processorId is required");

                var service = ctx.RequestServices.GetRequiredKeyedService<IAdminQueryService>(moduleKey);
                await service.ClearDeadLettersAsync(processorId, ct);
                return Results.NoContent();
            }).WithName($"{moduleKey}_ClearDeadLetters");
        }
    }
}
