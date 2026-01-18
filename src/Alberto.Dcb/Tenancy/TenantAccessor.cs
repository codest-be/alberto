namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Default implementation of <see cref="ITenantAccessor"/> that reads from <see cref="TenantContext"/>.
/// </summary>
public sealed class TenantAccessor(TenantContext context) : ITenantAccessor
{
    /// <inheritdoc />
    public string TenantId => context.TenantId
        ?? throw new InvalidOperationException(
            "No tenant context available. Ensure tenant middleware is configured and the request includes a valid tenant identifier.");

    /// <inheritdoc />
    public string? TenantIdOrDefault => context.TenantId;

    /// <inheritdoc />
    public bool HasTenant => context.TenantId is not null;
}
