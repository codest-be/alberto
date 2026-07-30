using Microsoft.AspNetCore.Antiforgery;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;

namespace Alberto.Admin.Bff.Transforms;

/// <summary>
/// YARP response transform that sets the XSRF-TOKEN cookie on SPA responses
/// (development only — the catch-all Vite proxy route triggers this).
/// In production, StaticFileOptions.OnPrepareResponse sets the cookie directly.
/// </summary>
internal sealed partial class AntiforgeryTokenResponseTransform(
    IAntiforgery antiforgery,
    ILogger<AntiforgeryTokenResponseTransform> logger) : ResponseTransform
{
    public override ValueTask ApplyAsync(ResponseTransformContext context)
    {
        // Only inject the XSRF token on SPA shell responses.
        //
        // Match on the route id, not on the presence of a "catch-all" route value: the
        // api, mcp-passthrough and mcp-wellknown routes are catch-alls too, so keying on
        // the route value re-issued a token on every proxied API response. That rotates
        // the antiforgery cookie underneath the SPA, and two in-flight requests can then
        // invalidate each other's token and 400 for no reason.
        //
        // The spa route exists only in Development; in Production the SPA is served by
        // StaticFiles, whose OnPrepareResponse sets the cookie, and this transform
        // correctly never fires.
        var routeId = context.HttpContext.GetReverseProxyFeature().Route.Config.RouteId;
        if (!string.Equals(routeId, "spa", StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        // Prevent caching of responses that carry XSRF tokens
        context.HttpContext.Response.Headers.CacheControl = "no-cache, no-store";
        context.HttpContext.Response.Headers.Pragma = "no-cache";

        var tokenSet = antiforgery.GetAndStoreTokens(context.HttpContext);
        ArgumentNullException.ThrowIfNull(tokenSet.RequestToken);

        // XSRF-TOKEN must be HttpOnly=false so the SPA JavaScript can read it
        context.HttpContext.Response.Cookies.Append("XSRF-TOKEN", tokenSet.RequestToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = !IsDevEnvironment(context.HttpContext),
            SameSite = SameSiteMode.Strict,
        });

        LogXsrfTokenAdded(logger, context.HttpContext.Request.Path.Value);
        return ValueTask.CompletedTask;
    }

    private static bool IsDevEnvironment(HttpContext context) =>
        context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "XSRF token added to SPA response for path: {RequestPath}")]
    private static partial void LogXsrfTokenAdded(ILogger logger, string? requestPath);
}
