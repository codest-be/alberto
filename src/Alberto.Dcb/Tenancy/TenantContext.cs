namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Holds tenant information for the current scope.
/// Registered as scoped to ensure each request has its own tenant context.
/// </summary>
public sealed class TenantContext
{
    // The check delegates to IdentifierRules.IsValidIdentifier so the regex is defined and
    // maintained in a single place, shared with module-key and shard-id validation.
    // See upgrade-notes/P1.5.md if your tenant IDs use a different format.

    private string? _tenantId;

    /// <summary>
    /// The tenant-ID rule in words, for callers that reject a bad tenant before it reaches
    /// <see cref="SetTenant"/> and have to say why.
    /// </summary>
    /// <remarks>
    /// Points at <see cref="IdentifierRules.Rule"/> so the wording stays consistent across all
    /// Alberto identifier error messages without duplicating the text.
    /// </remarks>
    public const string TenantIdRule = IdentifierRules.Rule;

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
        IdentifierRules.IsValidIdentifier(tenantId);

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
