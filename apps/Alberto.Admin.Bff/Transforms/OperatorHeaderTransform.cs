using System.Security.Claims;
using Yarp.ReverseProxy.Transforms;

namespace Alberto.Admin.Bff.Transforms;

/// <summary>
/// YARP request transform that forwards the authenticated operator's name
/// as an X-Alberto-Operator header so the API can attribute audit trail entries.
/// Applied to proxied routes only (graphql, api). Not applied to mcp-passthrough
/// or mcp-wellknown — those are skipped in AddTransforms.
/// </summary>
/// <remarks>
/// When the BFF runs anonymous — its default, until an identity provider is wired
/// into <c>AdminBffAuthentication</c> — there is no principal and the header is
/// omitted, so the API attributes the action to its own <c>DefaultOperatorId</c>.
/// That is the intended behaviour: an open admin surface should say "admin-panel"
/// in the audit trail rather than name someone it never actually identified.
/// </remarks>
internal sealed class OperatorHeaderTransform : RequestTransform
{
    public override ValueTask ApplyAsync(RequestTransformContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            // No fallback to a server-side value. A principal that carries neither a
            // name nor a subject cannot be attributed, and stamping the account the
            // BFF process happens to run under would put a local identity on a
            // remote user's action.
            var operatorName = context.HttpContext.User.Identity.Name
                ?? context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!string.IsNullOrEmpty(operatorName))
            {
                context.ProxyRequest.Headers.TryAddWithoutValidation("X-Alberto-Operator", operatorName);
            }
        }

        return ValueTask.CompletedTask;
    }
}
