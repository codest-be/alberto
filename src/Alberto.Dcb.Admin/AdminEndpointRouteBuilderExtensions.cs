using System.Reflection;
using System.Text.Json;
using Alberto.Dcb.Admin.Api.Models;
using Alberto.Dcb.Admin.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Admin;

/// <summary>
/// Extension methods for mapping DCB Admin endpoints.
/// </summary>
public static class AdminEndpointRouteBuilderExtensions
{
    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon"
    };

    /// <summary>
    /// Maps the DCB Admin panel endpoints for all registered modules.
    /// </summary>
    public static IEndpointRouteBuilder MapDcbAdmin(this IEndpointRouteBuilder app)
    {
        var registry = app.ServiceProvider.GetRequiredService<IOptions<AdminModuleRegistry>>().Value;

        foreach (var (moduleKey, options) in registry.Modules)
        {
            MapModuleAdmin(app, moduleKey, options);
        }

        // Map global modules discovery endpoint
        MapModulesEndpoint(app, registry);

        return app;
    }

    private static void MapModulesEndpoint(IEndpointRouteBuilder app, AdminModuleRegistry registry)
    {
        // Determine base path from first module or use default
        var basePath = registry.Modules.Values.FirstOrDefault()?.BasePath.TrimEnd('/') ?? "/alberto";

        app.MapGet($"{basePath}/api/modules", () =>
        {
            var modules = registry.Modules.Select(m => new ModuleInfo(
                m.Key,
                m.Value.Title,
                m.Value.ReadOnly)).ToList();

            return Results.Ok(modules);
        })
        .WithName("GetModules")
        .WithTags("Modules");
    }

    /// <summary>
    /// Maps the DCB Admin panel endpoints for a specific module.
    /// </summary>
    public static IEndpointRouteBuilder MapDcbAdmin(
        this IEndpointRouteBuilder app,
        string moduleKey)
    {
        var registry = app.ServiceProvider.GetRequiredService<IOptions<AdminModuleRegistry>>().Value;

        if (!registry.Modules.TryGetValue(moduleKey, out var options))
        {
            throw new InvalidOperationException(
                $"Module '{moduleKey}' was not configured with WithAdmin().");
        }

        MapModuleAdmin(app, moduleKey, options);
        return app;
    }

    private static void MapModuleAdmin(
        IEndpointRouteBuilder app,
        string moduleKey,
        AdminOptions options)
    {
        var basePath = options.BasePath.TrimEnd('/');

        // Map API endpoints
        ProcessorsEndpoints.MapProcessorsEndpoints(app, basePath, moduleKey, options);
        CheckpointsEndpoints.MapCheckpointsEndpoints(app, basePath, moduleKey, options);
        DeadLettersEndpoints.MapDeadLettersEndpoints(app, basePath, moduleKey, options);
        ProjectionStatesEndpoints.MapProjectionStatesEndpoints(app, basePath, moduleKey, options);
        TenantsEndpoints.MapTenantsEndpoints(app, basePath, moduleKey, options);
        SystemEndpoints.MapSystemEndpoints(app, basePath, moduleKey, options);

        // Map embedded UI if enabled
        if (options.EnableUI)
        {
            MapEmbeddedUI(app, basePath, moduleKey, options);
        }
    }

    private static void MapEmbeddedUI(
        IEndpointRouteBuilder app,
        string basePath,
        string moduleKey,
        AdminOptions options)
    {
        var assembly = typeof(AdminEndpointRouteBuilderExtensions).Assembly;
        var resourcePrefix = "Alberto.Dcb.Admin.UI.EmbeddedResources.";

        // Serve config.js with injected configuration
        app.MapGet($"{basePath}/{moduleKey}/config.js", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/javascript; charset=utf-8";
            var config = new
            {
                basePath,
                moduleKey,
                readOnly = options.ReadOnly,
                title = options.Title
            };
            return $"window.DCB_ADMIN_CONFIG = {JsonSerializer.Serialize(config)};";
        }).ExcludeFromDescription();

        // Serve UI and static assets
        app.MapGet($"{basePath}/{moduleKey}/{{*path}}", async (HttpContext ctx, string? path) =>
        {
            // Redirect to trailing slash for proper relative URL resolution
            if (path is null && !ctx.Request.Path.Value!.EndsWith('/'))
            {
                ctx.Response.Redirect($"{ctx.Request.Path}/", permanent: false);
                return;
            }

            // If no path or empty, serve index.html
            if (string.IsNullOrEmpty(path))
            {
                await ServeResource(ctx, assembly, $"{resourcePrefix}index.html");
                return;
            }

            // Prevent path traversal
            if (path.Contains("..") || path.StartsWith("/"))
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            // Skip API routes
            if (path.StartsWith("api/"))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            var resourceName = $"{resourcePrefix}{path.Replace('/', '.')}";
            await ServeResource(ctx, assembly, resourceName);
        }).ExcludeFromDescription();
    }

    private static async Task ServeResource(
        HttpContext context,
        Assembly assembly,
        string resourceName)
    {
        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var extension = Path.GetExtension(resourceName);
        if (ContentTypes.TryGetValue(extension, out var contentType))
        {
            context.Response.ContentType = contentType;
        }

        context.Response.Headers.CacheControl = "no-cache";
        await stream.CopyToAsync(context.Response.Body);
    }
}
