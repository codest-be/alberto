namespace Alberto.Tenancy;

/// <summary>
/// Provides access to the current tenant context.
/// </summary>
public interface ITenantAccessor
{
    /// <summary>
    /// Gets the current tenant ID. Throws if no tenant context is set.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no tenant context is available.</exception>
    string TenantId { get; }

    /// <summary>
    /// Gets the current tenant ID, or null if not set.
    /// </summary>
    string? TenantIdOrDefault { get; }

    /// <summary>
    /// Returns true if a tenant context is available.
    /// </summary>
    bool HasTenant { get; }
}
