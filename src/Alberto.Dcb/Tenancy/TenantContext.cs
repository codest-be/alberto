using System.Text.RegularExpressions;

namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Holds tenant information for the current scope.
/// Registered as scoped to ensure each request has its own tenant context.
/// </summary>
public sealed class TenantContext
{
    // Mirrors the schema-name allowlist in SchemaQualifier: lowercase letter start,
    // lowercase alphanumeric + underscore, max 63 characters.
    // This rejects UUIDs, hyphens, uppercase, and other characters that could indicate
    // a mis-configured or injected tenant identifier.
    // See upgrade-notes/P1.5.md if your tenant IDs use a different format.
    private static readonly Regex TenantIdPattern =
        new(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled, matchTimeout: TimeSpan.FromMilliseconds(100));

    private string? _tenantId;

    /// <summary>
    /// Gets the current tenant ID, or null if not set.
    /// </summary>
    public string? TenantId => _tenantId;

    /// <summary>
    /// Sets the tenant ID for the current scope.
    /// </summary>
    /// <param name="tenantId">
    /// The tenant ID to set. Must match <c>^[a-z][a-z0-9_]{0,62}$</c>: a lowercase letter
    /// followed by up to 62 lowercase alphanumeric characters or underscores.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tenantId"/> is null, whitespace, or does not match the
    /// allowlist pattern.
    /// </exception>
    public void SetTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!TenantIdPattern.IsMatch(tenantId))
            throw new ArgumentException(
                $"Tenant ID '{tenantId}' is invalid. " +
                "Tenant IDs must start with a lowercase letter and contain only lowercase letters, " +
                "digits, and underscores, with a maximum length of 63 characters. " +
                "See upgrade-notes/P1.5.md if your application uses a different tenant ID format.",
                nameof(tenantId));

        _tenantId = tenantId;
    }
}
