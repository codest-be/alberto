namespace Alberto.EntityFramework;

/// <summary>
/// Names of the EF Core query filters Alberto installs on projection entities.
/// </summary>
public static class ProjectionQueryFilters
{
    /// <summary>
    /// The named query filter that keeps a projection entity's reads on its active rebuild
    /// version. Installed by
    /// <see cref="EfProjectionModelBuilderExtensions.ProjectionEntity{TEntity}(Microsoft.EntityFrameworkCore.ModelBuilder, string, System.Action{Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder{TEntity}}?, string?)"/>.
    /// </summary>
    /// <remarks>
    /// To see every version, for example in an operator tool that inspects a rebuild in
    /// flight, turn off this filter alone so any filters of your own still apply:
    /// <code>
    /// context.OrderSummaries.IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
    /// </code>
    /// </remarks>
    public const string ActiveVersion = "alberto-active-version";
}
