namespace Alberto.Admin.Bff;

/// <summary>
/// The admin BFF's authentication seam.
/// </summary>
/// <remarks>
/// <para>
/// Out of the box the admin surface is <b>anonymous</b>: no authentication scheme is
/// registered, the default authorization policy allows every request, and /bff/login is
/// not mapped. That is deliberate. The BFF ships as plain ASP.NET Core wiring with
/// nothing bespoke in front of it, and it does not fake a sign-in — an admin surface
/// that looked authenticated while accepting anyone would be worse than one that is
/// honestly open.
/// </para>
/// <para>
/// To put a real identity provider in front of it — Keycloak, Auth0, Entra ID, anything
/// that speaks OIDC — register the handlers in <see cref="AddAdminAuthentication"/> and
/// return <see langword="true"/>. Everything downstream follows from that one return
/// value: the default policy starts requiring an authenticated user, so the YARP routes
/// already marked <c>"AuthorizationPolicy": "Default"</c> begin demanding sign-in with no
/// change to appsettings.json; /bff/login and /bff/logout are mapped and challenge through
/// the default scheme; and /bff/user starts answering 401, which is what makes the SPA
/// render its sign-in prompt.
/// </para>
/// </remarks>
public static class AdminBffAuthentication
{
    /// <summary>
    /// Registers the admin surface's authentication schemes, if any.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a scheme was registered and the admin surface should
    /// require sign-in; <see langword="false"/> to run anonymous.
    /// </returns>
    public static bool AddAdminAuthentication(this WebApplicationBuilder builder)
    {
        // Called unconditionally: UseAuthentication throws without the services this
        // registers, and with no scheme and no default the middleware is a no-op. The
        // request pipeline is then identical in both modes, so wiring a provider changes
        // only what the schemes do — never the shape of the pipeline around them.
        builder.Services.AddAuthentication();

        // ── HOOK ─────────────────────────────────────────────────────────────────
        // Replace the body below to wire an identity provider. Keycloak and Auth0 both
        // fit the same OIDC shape; only the authority and client details differ:
        //
        //   builder.Services
        //       .AddAuthentication(options =>
        //       {
        //           options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        //           options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        //           options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
        //       })
        //       .AddCookie(options =>
        //       {
        //           options.Cookie.Name = "alberto.admin.session";
        //           options.Cookie.HttpOnly = true;
        //           options.Cookie.SameSite = SameSiteMode.Lax;
        //           options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        //           options.SlidingExpiration = true;
        //
        //           // A SPA's fetch() cannot follow a redirect to an identity provider —
        //           // it would either fail CORS or resolve with the login page as the
        //           // body. Answer API calls with 401 and let the SPA challenge, while
        //           // document requests still redirect normally.
        //           options.Events.OnRedirectToLogin = context =>
        //           {
        //               if (context.Request.Path.StartsWithSegments("/api"))
        //               {
        //                   context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        //                   return Task.CompletedTask;
        //               }
        //
        //               context.Response.Redirect(context.RedirectUri);
        //               return Task.CompletedTask;
        //           };
        //       })
        //       .AddOpenIdConnect(options =>
        //       {
        //           options.Authority = builder.Configuration["Admin:Oidc:Authority"];
        //           options.ClientId = builder.Configuration["Admin:Oidc:ClientId"];
        //           options.ClientSecret = builder.Configuration["Admin:Oidc:ClientSecret"];
        //           options.ResponseType = "code";
        //           options.UsePkce = true;
        //           options.SaveTokens = true;
        //           options.GetClaimsFromUserInfoEndpoint = true;
        //       });
        //
        //   return true;
        //
        // AddOpenIdConnect lives in Microsoft.AspNetCore.Authentication.OpenIdConnect,
        // which this project does not reference — the BFF takes no dependency on a
        // provider nobody has chosen yet. Add the package when you wire one up.
        // ─────────────────────────────────────────────────────────────────────────
        return false;
    }
}
