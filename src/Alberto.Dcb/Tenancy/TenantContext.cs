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
    /// The tenant-ID rule in words, for callers that reject a bad tenant before it reaches
    /// <see cref="SetTenant"/> and have to say why.
    /// </summary>
    public const string TenantIdRule =
        "Tenant IDs must start with a lowercase letter and contain only lowercase letters, " +
        "digits, and underscores, with a maximum length of 63 characters.";

    /// <summary>
    /// Gets the current tenant ID, or null if not set.
    /// </summary>
    public string? TenantId => _tenantId;

    /// <summary>
    /// Whether <paramref name="tenantId"/> is one <see cref="SetTenant"/> will accept.
    /// </summary>
    /// <remarks>
    /// The rule is asked for rather than restated because a caller that guesses it wrong in the
    /// permissive direction turns a clean rejection into an unhandled
    /// <see cref="ArgumentException"/> from <see cref="SetTenant"/> further down the request.
    /// The Orders API's tenant interceptor did exactly that: it allowed hyphens, so
    /// <c>X-Tenant-Id: a-b-c</c> passed its check and then threw an opaque server error.
    /// </remarks>
    public static bool IsValidTenantId(string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) && TenantIdPattern.IsMatch(tenantId);

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

        if (!IsValidTenantId(tenantId))
            throw new ArgumentException(
                $"Tenant ID '{tenantId}' is invalid. {TenantIdRule} " +
                "See upgrade-notes/P1.5.md if your application uses a different tenant ID format.",
                nameof(tenantId));

        _tenantId = tenantId;
    }
}
