using Alberto.Dcb.Tenancy;
using HotChocolate.AspNetCore;
using HotChocolate.Execution;

namespace Alberto.Orders.Api.GraphQL;

/// <summary>
/// Extracts tenant ID from X-Tenant-Id header and sets it in the request context.
/// Rejects requests that don't include a valid tenant header.
/// </summary>
public sealed class TenantHttpRequestInterceptor : DefaultHttpRequestInterceptor
{
    public const string TenantIdHeader = "X-Tenant-Id";
    public const string TenantIdKey = "TenantId";

    public override ValueTask OnCreateAsync(
        HttpContext context,
        IRequestExecutor requestExecutor,
        OperationRequestBuilder requestBuilder,
        CancellationToken cancellationToken)
    {
        var tenantId = context.Request.Headers[TenantIdHeader].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage($"Missing required header: {TenantIdHeader}")
                    .SetCode("MISSING_TENANT")
                    .Build());
        }

        // Ask TenantContext rather than restate its rule. This check used to allow dashes,
        // uppercase, and 64 characters — all things SetTenant rejects two lines below — so
        // `X-Tenant-Id: a-b-c` passed here and then threw an ArgumentException the GraphQL
        // pipeline reported as "Unexpected Execution Error", with the real reason only in the
        // server log.
        if (!TenantContext.IsValidTenantId(tenantId))
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage($"Invalid tenant ID format. {TenantContext.TenantIdRule}")
                    .SetCode("INVALID_TENANT")
                    .Build());
        }

        // Set tenant in scoped TenantContext for DI
        var tenantContext = context.RequestServices.GetRequiredService<TenantContext>();
        tenantContext.SetTenant(tenantId);

        // Also store in GraphQL global state for resolver access
        requestBuilder.SetGlobalState(TenantIdKey, tenantId);

        return base.OnCreateAsync(context, requestExecutor, requestBuilder, cancellationToken);
    }
}
