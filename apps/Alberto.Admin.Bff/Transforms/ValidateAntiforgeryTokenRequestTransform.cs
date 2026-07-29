using Microsoft.AspNetCore.Antiforgery;
using Yarp.ReverseProxy.Transforms;

namespace Alberto.Admin.Bff.Transforms;

/// <summary>
/// YARP request transform that validates the double-submit CSRF token on
/// mutating (POST/PUT/PATCH/DELETE) proxied requests before forwarding.
/// Applied to authenticated browser-facing routes only. Anonymous routes and the
/// mcp-passthrough route are skipped in AddTransforms — see the rationale there.
/// </summary>
internal sealed partial class ValidateAntiforgeryTokenRequestTransform(
    IAntiforgery antiforgery,
    ILogger<ValidateAntiforgeryTokenRequestTransform> logger) : RequestTransform
{
    public override async ValueTask ApplyAsync(RequestTransformContext context)
    {
        // Skip validation for safe (read-only) HTTP methods
        if (HttpMethods.IsGet(context.HttpContext.Request.Method)
            || HttpMethods.IsHead(context.HttpContext.Request.Method)
            || HttpMethods.IsOptions(context.HttpContext.Request.Method)
            || HttpMethods.IsConnect(context.HttpContext.Request.Method)
            || HttpMethods.IsTrace(context.HttpContext.Request.Method))
        {
            return;
        }

        // Skip validation for Bearer-authenticated requests — CSRF only applies to
        // cookie-based auth where the browser sends credentials automatically.
        var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            LogAntiforgeryValidationFailed(logger, ex, context.HttpContext.Request.Path.Value);
            context.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Antiforgery token validation failed for request path: {RequestPath}")]
    private static partial void LogAntiforgeryValidationFailed(
        ILogger logger,
        Exception ex,
        string? requestPath);
}
