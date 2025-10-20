using Alberto.EventStore.MultiTenant;

namespace Alberto.Example;

public sealed class TenantMiddleware(RequestDelegate next)
{
    private const string TenantHeaderName = "X-Tenant";

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.Request.Headers.TryGetValue(TenantHeaderName, out var tenantId) &&
            !string.IsNullOrWhiteSpace(tenantId))
        {
            tenantContext.Tenant = new Tenant(tenantId.ToString());
        }

        await next(context);
    }
}