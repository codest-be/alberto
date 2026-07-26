namespace Alberto.Dcb.Tenancy;

/// <summary>
/// The tenant identifiers a projection's state rows can be stored under, for the cases where
/// the storing code is not scoped to one of the module's real tenants.
/// </summary>
/// <remarks>
/// <para>
/// A tenant-enabled module's projection table declares <c>tenant_id NOT NULL</c> and keys rows
/// by it. That column has to be given a value even by a projection that deliberately blends
/// every tenant into one document — a running total, a global counter, an overview. There is no
/// real tenant to attribute such a document to, so it is stored under
/// <see cref="CrossTenant"/> instead.
/// </para>
/// <para>
/// The value is chosen to be unusable as a real tenant id. Tenant ids arrive from the
/// <c>X-Tenant-Id</c> header and are used verbatim, so any legal header value could collide
/// with a plausible sentinel; a bare <c>*</c> cannot be mistaken for a tenant name in a
/// query result or a log line.
/// </para>
/// </remarks>
public static class TenantScope
{
    /// <summary>
    /// The tenant a cross-tenant projection stores its documents under.
    /// </summary>
    /// <remarks>
    /// Pass this as the <c>tenantId</c> of a state store whose projection produces one document
    /// for all tenants, and read it back with the same value. A projection that produces one
    /// document set <em>per</em> tenant passes the real tenant instead. Prefer
    /// <see cref="CrossTenantFor"/>, which also gets the no-tenancy case right.
    /// </remarks>
    public const string CrossTenant = "*";

    /// <summary>
    /// The tenant id a cross-tenant projection should store its document under, given the
    /// tenant its consumer is currently running for: <see cref="CrossTenant"/> when the module
    /// is tenant-enabled, <see langword="null"/> when it declared no tenancy.
    /// </summary>
    /// <remarks>
    /// The distinction is not cosmetic — the two produce different SQL against different primary
    /// keys. A store given <see cref="CrossTenant"/> against a single-tenant schema queries a
    /// <c>tenant_id</c> column that does not exist; a store given <see langword="null"/> against
    /// a tenant-enabled schema omits a <c>NOT NULL</c> column and names a primary key that does
    /// not match. Both fail at the first write, which is how this method came to exist.
    /// </remarks>
    /// <param name="consumerTenantId">
    /// The tenant the consumer is building a store for: the tenant of the events about to be
    /// applied, or <see langword="null"/> in a module without tenancy.
    /// </param>
    public static string? CrossTenantFor(string? consumerTenantId) =>
        consumerTenantId is null ? null : CrossTenant;
}
