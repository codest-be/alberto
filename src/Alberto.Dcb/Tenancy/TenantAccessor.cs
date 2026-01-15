namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Default implementation of <see cref="ITenantAccessor"/> that reads from <see cref="TenantContext"/>.
/// </summary>
public sealed class TenantAccessor : ITenantAccessor
{
    private readonly TenantContext _context;

    public TenantAccessor(TenantContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public string TenantId => _context.TenantId
        ?? throw new InvalidOperationException(
            "No tenant context available. Ensure tenant middleware is configured and the request includes a valid tenant identifier.");

    /// <inheritdoc />
    public string? TenantIdOrDefault => _context.TenantId;

    /// <inheritdoc />
    public bool HasTenant => _context.TenantId is not null;
}
