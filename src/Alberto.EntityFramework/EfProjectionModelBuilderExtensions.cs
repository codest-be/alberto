using Alberto.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alberto.EntityFramework;

/// <summary>
/// Model configuration for projection entities.
/// </summary>
public static class EfProjectionModelBuilderExtensions
{
    /// <summary>
    /// Configures a projection entity so it can take part in zero-downtime rebuilds: the key
    /// becomes <c>(DocumentId, RebuildVersion)</c>, and the version column gets a default so
    /// existing rows read as version 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this from <c>OnModelCreating</c> for every entity registered with
    /// <c>AddEfProjection</c>:
    /// </para>
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ProjectionEntity&lt;OrderSummaryEntity&gt;(entity =>
    ///     {
    ///         entity.ToTable("order_summaries");
    ///         entity.Property(e => e.CustomerName).HasMaxLength(200);
    ///     });
    /// }
    /// </code>
    /// <para>
    /// Without the version in the key, a shadow rebuild loop's rows collide with the live
    /// rows on insert and the rebuild overwrites the projection it was supposed to be
    /// shadowing. <see cref="EfStateStore{TEntity,TDbContext}"/> filters every read by
    /// version on the assumption that this has been applied.
    /// </para>
    /// <para>
    /// Adding this to an existing model is a schema change: generate an EF migration for it.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The projection entity type.</typeparam>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="configure">
    /// Optional additional configuration, applied after the rebuild-related parts so it can
    /// override them.
    /// </param>
    public static ModelBuilder ProjectionEntity<TEntity>(
        this ModelBuilder modelBuilder,
        Action<EntityTypeBuilder<TEntity>>? configure = null)
        where TEntity : class, IProjectionEntity
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entity = modelBuilder.Entity<TEntity>();

        entity.HasKey(e => new { e.DocumentId, e.RebuildVersion });
        entity.Property(e => e.RebuildVersion).HasDefaultValue(ProjectionVersions.Initial);

        // The live version is the only one anything queries by document id, and a rebuild
        // doubles the row count while it runs, so the version leads the index.
        entity.HasIndex(e => new { e.RebuildVersion, e.UpdatedAt });

        configure?.Invoke(entity);

        return modelBuilder;
    }
}
