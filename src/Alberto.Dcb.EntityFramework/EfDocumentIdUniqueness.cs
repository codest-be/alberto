namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// What the caller guarantees about the document ids a projection declaration produces, on a
/// module that declared <c>.WithTenancy()</c>.
/// </summary>
/// <remarks>
/// <para>
/// An EF projection stores its state in a table Alberto does not own, keyed by
/// <see cref="Subscriptions.IProjectionEntity.DocumentId"/> and
/// <see cref="Subscriptions.IProjectionEntity.RebuildVersion"/> — the only two columns the
/// interface has. Both the async store and the inline projection therefore load and write by
/// those two columns alone, with no tenant predicate available to them. Adding a
/// <c>TenantId</c> column to the entity does not change that: the store still cannot see it.
/// </para>
/// <para>
/// So on a tenant-enabled module the projection is only correct if two tenants can never
/// produce the same document id. That is a property of the declaration, not of the schema, and
/// nothing can verify it — which is why it has to be stated rather than assumed.
/// </para>
/// </remarks>
public enum EfDocumentIdUniqueness
{
    /// <summary>
    /// Nothing is guaranteed. The default, and the only correct value on a single-tenant
    /// module, where there is no second tenant to collide with. On a module that declared
    /// <c>.WithTenancy()</c>, registration fails with <c>ALB0027</c>.
    /// </summary>
    NotDeclared = 0,

    /// <summary>
    /// Every document id this declaration produces is unique across every tenant — a GUID
    /// aggregate id, or an id the handler prefixes with a tenant discriminator carried on the
    /// event itself. Two tenants sharing one id would share one row.
    /// </summary>
    AcrossTenants = 1,
}
