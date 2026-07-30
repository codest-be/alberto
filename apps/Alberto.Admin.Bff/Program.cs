using Alberto.Admin.Bff;
using Alberto.Admin.Bff.Transforms;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using ServiceDefaults;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false;
});

builder.AddServiceDefaults();

// Suppress noisy YARP forwarder logs for Vite dev server proxy requests
builder.Logging.AddFilter("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.Warning);

// In Development, override the Vite cluster destination from the Aspire service-discovery
// env var injected by WithReference(adminDashboard) in the AppHost.
// Fall back to the local Vite default port so the BFF works without Aspire.
if (builder.Environment.IsDevelopment())
{
    var viteUrl = builder.Configuration["services__admin-dashboard__http__0"]
        ?? "http://localhost:5174";
    builder.Configuration["ReverseProxy:Clusters:vite:Destinations:vite:Address"] = viteUrl;
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 2;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
});

// YARP reverse proxy — loads route table from appsettings.json (+ appsettings.Development.json)
// and resolves the albertoapi cluster via Aspire service discovery.
builder.Services.AddSingleton<AntiforgeryTokenResponseTransform>();
builder.Services.AddSingleton<ValidateAntiforgeryTokenRequestTransform>();
builder.Services.AddSingleton<OperatorHeaderTransform>();

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        // CSRF response transform: sets XSRF-TOKEN cookie on SPA catch-all responses.
        // In development the catch-all goes to the Vite dev server; in production it
        // is not a YARP route (StaticFiles + MapFallbackToFile handle it, and
        // OnPrepareResponse sets the cookie there).
        context.ResponseTransforms.Add(context.Services.GetRequiredService<AntiforgeryTokenResponseTransform>());

        // Anonymous routes (mcp-wellknown, spa) bypass BFF auth transforms. OAuth
        // resource-metadata discovery must stay reachable without credentials, and the
        // spa route serves the shell that has yet to sign in.
        if (string.IsNullOrEmpty(context.Route.AuthorizationPolicy))
        {
            return;
        }

        // CSRF: validate the double-submit token on mutating requests.
        //
        // Exempt the MCP route. It is authenticated (Default policy) but its clients are
        // not browsers: they hold no XSRF-TOKEN cookie and cannot be made to read one, so
        // validating here would reject every legitimate call. The cross-site risk that
        // antiforgery covers does not apply — a browser cannot POST application/json to
        // another origin without a CORS preflight, and this BFF registers no CORS policy.
        if (!string.Equals(context.Route.RouteId, "mcp-passthrough", StringComparison.Ordinal))
        {
            context.RequestTransforms.Add(context.Services.GetRequiredService<ValidateAntiforgeryTokenRequestTransform>());
        }

        // Strip session cookies — the API authenticates via X-Alberto-Operator, not cookies
        context.RequestTransforms.Add(new RequestHeaderRemoveTransform("Cookie"));

        // Forward the authenticated operator's name for API audit trail attribution
        context.RequestTransforms.Add(context.Services.GetRequiredService<OperatorHeaderTransform>());
    })
    .AddServiceDiscoveryDestinationResolver();

// Authentication. Anonymous by default — see AdminBffAuthentication for the hook
// that wires Keycloak, Auth0 or any other OIDC provider.
var requireAuthentication = builder.AddAdminAuthentication();

// Antiforgery: double-submit cookie pattern.
// Cookie:      alberto.antiforgery (HttpOnly, framework-managed, not readable by JS)
// Header:      X-XSRF-TOKEN (sent by SPA on mutating requests)
// Readable:    XSRF-TOKEN (non-HttpOnly cookie, read by SPA to build the header)
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "alberto.antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.None;
});

// Authorization. The proxied routes are marked "AuthorizationPolicy": "Default" in
// appsettings.json, and what Default *means* is decided here by whether an identity
// provider was registered above. Anonymous until one is, sign-in-required the moment
// there is one — the route table never changes.
//
// Neither policy names an authentication scheme. A policy that lists one makes
// AuthorizationMiddleware re-run authentication for that scheme and *replace*
// context.User with the result, discarding whatever UseAuthentication already
// resolved. With no scheme listed the middleware authorizes the ambient principal.
builder.Services
    .AddAuthorizationBuilder()
    .SetDefaultPolicy(requireAuthentication
        ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()
        : new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build());

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWebSockets();

if (!app.Environment.IsDevelopment())
{
    // Production: serve the pre-built SPA from wwwroot.
    // index.html is served with no-store so a deploy that changes the hashed
    // JS/CSS bundle filenames never leaves stale HTML in a browser cache.
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Context.Response.Headers.CacheControl = "no-store, must-revalidate";

                // Set the XSRF-TOKEN cookie on the initial page load so the SPA can
                // read it and attach X-XSRF-TOKEN on subsequent mutating requests.
                var antiforgery = ctx.Context.RequestServices.GetRequiredService<IAntiforgery>();
                antiforgery.GetAndStoreTokens(ctx.Context);
            }
        },
    });
}

// With no scheme registered and no default, this resolves no principal and falls
// through — the anonymous case costs nothing and needs no branch.
app.UseAuthentication();
app.UseAuthorization();

// ── BFF endpoints ─────────────────────────────────────────────────────────────
var bff = app.MapGroup("/bff").AllowAnonymous();

// Sign-in and sign-out exist only when there is somewhere to sign in to. Mapping
// them in anonymous mode would give the SPA a link that challenges a scheme that
// was never registered, which throws at request time rather than failing honestly.
if (requireAuthentication)
{
    // Both endpoints name no scheme: they go through the defaults configured in
    // AddAdminAuthentication, so swapping Keycloak for Auth0 — or for anything else
    // that speaks OIDC — needs no change here.
    bff.MapMethods("login", [HttpMethods.Get, HttpMethods.Post], (HttpContext context) =>
    {
        // Relative-only: an absolute returnUrl would turn this into an open redirect,
        // and the sign-in flow is exactly where a user is least likely to notice
        // having been sent somewhere else.
        var returnUrl = context.Request.Query["returnUrl"].ToString();
        var redirectTarget = !string.IsNullOrEmpty(returnUrl)
            && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                ? returnUrl
                : "/";

        return Results.Challenge(new AuthenticationProperties { RedirectUri = redirectTarget });
    });

    bff.MapPost("logout", () => Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" }));
}

// Called by the SPA on boot to decide what to render.
//
// The three answers are deliberately distinct. 401 means "an identity provider is
// configured and you are not signed in" — the only case where a sign-in prompt is
// the right thing to show. A 200 with authenticationRequired: false means the admin
// surface is running anonymous, and the SPA renders the app: prompting for a sign-in
// that cannot happen would strand the operator on a dead button.
bff.MapGet("user", (HttpContext context) =>
{
    var authenticated = context.User.Identity?.IsAuthenticated ?? false;

    if (requireAuthentication && !authenticated)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        authenticationRequired = requireAuthentication,
        name = authenticated ? context.User.Identity!.Name : null,
        claims = context.User.Claims.Select(c => new { c.Type, c.Value }).ToList(),
    });
});

// ── YARP reverse proxy ────────────────────────────────────────────────────────
app.MapReverseProxy();

// ── Production: SPA catch-all ─────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    // Client-side routes (React Router, etc.) all map back to index.html.
    // The XSRF-TOKEN cookie is set here too so deep-linked entry points also
    // receive it on first load.
    app.MapFallbackToFile("/index.html", new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-store, must-revalidate";

            var antiforgery = ctx.Context.RequestServices.GetRequiredService<IAntiforgery>();
            antiforgery.GetAndStoreTokens(ctx.Context);
        },
    });
}

app.MapDefaultEndpoints();

app.Run();
