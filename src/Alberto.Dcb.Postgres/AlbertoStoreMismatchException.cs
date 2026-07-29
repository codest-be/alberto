namespace Alberto.Dcb.Postgres;

/// <summary>
/// Thrown when a module's declaration contradicts what the store it is pointed at already is.
/// </summary>
/// <remarks>
/// Distinct from a migration failure: nothing was run. The store is exactly as it was, and the
/// remedy is to change the declaration or to point it somewhere else — never to retry. Derives
/// from <see cref="InvalidOperationException"/> because that is what the tenancy check threw
/// before it moved ahead of the migration.
/// </remarks>
public sealed class AlbertoStoreMismatchException : InvalidOperationException
{
    /// <summary>Creates the exception with an already-composed diagnostic.</summary>
    public AlbertoStoreMismatchException(string message) : base(message)
    {
    }

    /// <summary>
    /// Composes the ALB0021 diagnostic for a store whose tenancy mode does not match the one
    /// the module declares.
    /// </summary>
    /// <param name="schemaName">The schema the store lives in, as it appears in catalog queries.</param>
    /// <param name="recorded">What the store is, and how that was established.</param>
    /// <param name="declaredSingleTenant">What the module declares.</param>
    internal static AlbertoStoreMismatchException ForTenancy(
        string schemaName,
        RecordedTenancy recorded,
        bool declaredSingleTenant)
    {
        var stored = recorded.SingleTenant ? "single-tenant" : "multi-tenant";
        var declaration = declaredSingleTenant
            ? "the module does not declare .WithTenancy()"
            : "the module declares .WithTenancy()";

        var wanted = declaredSingleTenant ? "single-tenant" : "multi-tenant";
        var keep = declaredSingleTenant
            ? "add .WithTenancy() to the module declaration"
            : "remove .WithTenancy() from the module declaration";

        return new AlbertoStoreMismatchException(
            $"""
             ALB0021: Alberto store tenancy mismatch in schema '{schemaName}'.

               The store was created {stored}; {declaration}.
               ({Attribution(recorded)})

               Single-tenant and multi-tenant are separate schemas, not a setting. There is no
               in-place migration between them, and no backfill for tenant_id on existing events.

               To run {wanted}, point this module at a new database and replay into it.
               To keep this store, {keep}.

               No migration scripts were run.
             """);
    }

    /// <summary>
    /// Names the source the recorded value came from, so an operator can tell whether the
    /// imprint or the schema itself is what disagrees with them.
    /// </summary>
    private static string Attribution(RecordedTenancy recorded) => recorded switch
    {
        { Source: TenancySource.Imprint } =>
            $"Recorded in {StoreImprint.TableName}.",
        { SingleTenant: true } =>
            "Inferred from alberto_events, which has no tenant_id column.",
        _ =>
            "Inferred from alberto_events.tenant_id.",
    };
}
