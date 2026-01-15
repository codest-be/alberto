namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Holds tenant information for the current scope.
/// Registered as scoped to ensure each request has its own tenant context.
/// </summary>
public sealed class TenantContext
{
    private string? _tenantId;

    /// <summary>
    /// Gets the current tenant ID, or null if not set.
    /// </summary>
    public string? TenantId => _tenantId;

    /// <summary>
    /// Sets the tenant ID for the current scope.
    /// </summary>
    /// <param name="tenantId">The tenant ID to set.</param>
    /// <exception cref="ArgumentException">Thrown when tenant ID is null or whitespace.</exception>
    public void SetTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        _tenantId = tenantId;
    }
}
