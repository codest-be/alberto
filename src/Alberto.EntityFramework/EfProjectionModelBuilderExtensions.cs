using System.Diagnostics.CodeAnalysis;
using Alberto.Projections;
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
    /// <para>
    /// This overload does <b>not</b> filter application reads: a plain <c>Set&lt;TEntity&gt;()</c>
    /// returns every version in the table, so while a rebuild runs it sees each document twice.
    /// Prefer the overload that takes the processor id, which filters reads to the active version.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The projection entity type.</typeparam>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="configure">
    /// Optional additional configuration, applied after the rebuild-related parts so it can
    /// override them.
    /// </param>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "This overload shipped before the filtered one. The required string " +
                        "processorId keeps the two unambiguous at every call site.")]
    public static ModelBuilder ProjectionEntity<TEntity>(
        this ModelBuilder modelBuilder,
        Action<EntityTypeBuilder<TEntity>>? configure = null)
        where TEntity : class, IProjectionEntity
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureVersionedKey(modelBuilder.Entity<TEntity>());
        configure?.Invoke(modelBuilder.Entity<TEntity>());

        return modelBuilder;
    }

    /// <summary>
    /// Configures a projection entity for zero-downtime rebuilds, as the other overload does,
    /// and makes application reads see only the active rebuild version by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While a rebuild runs, the table holds two rows per document: the live one and the one the
    /// shadow loop is filling. This overload adds a named query filter,
    /// <see cref="ProjectionQueryFilters.ActiveVersion"/>, so a plain query only returns rows of
    /// the processor's active version:
    /// </para>
    /// <code>
    /// modelBuilder.ProjectionEntity&lt;OrderSummaryEntity&gt;(nameof(OrderSummaryProjection), entity =>
    ///     entity.ToTable("order_summaries"));
    ///
    /// // Later, anywhere:
    /// context.Set&lt;OrderSummaryEntity&gt;()                       // active version only
    /// context.Set&lt;OrderSummaryEntity&gt;()
    ///     .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])  // every version
    /// </code>
    /// <para>
    /// The active version is read in SQL on every query, from <c>alberto_projection_rebuild_meta</c>,
    /// so a promotion is visible to the very next query, on every replica, with no refresh interval.
    /// A processor with no row there has never been rebuilt and reads
    /// <see cref="ProjectionVersions.Initial"/>.
    /// </para>
    /// <para>
    /// <b>That table must be reachable from this <c>DbContext</c>'s connection</b>: the event store
    /// has to be <c>Alberto.Postgres</c>, in the same database, with its migrations applied. The
    /// table is mapped read-only and excluded from EF migrations. If the event store lives
    /// elsewhere (the in-memory backend, another database), use the other overload; this one
    /// would fail every query with "relation does not exist".
    /// </para>
    /// <para>
    /// The filter is named, so it combines with filters of your own, and opting out with
    /// <c>IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])</c> leaves those in place.
    /// Alberto's own state store, inline projection and version clearer opt out exactly that way,
    /// because they address a version explicitly.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The projection entity type.</typeparam>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="processorId">
    /// The id of the projection that writes this entity, as given to <c>DeclareProjection.For</c>.
    /// The active version is tracked per processor.
    /// </param>
    /// <param name="configure">
    /// Optional additional configuration, applied after the rebuild-related parts so it can
    /// override them.
    /// </param>
    /// <param name="rebuildMetaSchema">
    /// The schema holding <c>alberto_projection_rebuild_meta</c>, i.e. the <c>Schema</c> given to
    /// <c>WithPostgres</c>. Defaults to the model's default schema.
    /// </param>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "See the suppression on the other overload.")]
    public static ModelBuilder ProjectionEntity<TEntity>(
        this ModelBuilder modelBuilder,
        string processorId,
        Action<EntityTypeBuilder<TEntity>>? configure = null,
        string? rebuildMetaSchema = null)
        where TEntity : class, IProjectionEntity
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        modelBuilder.Entity<ProjectionRebuildMetaRow>(meta =>
        {
            meta.HasNoKey();
            meta.ToTable("alberto_projection_rebuild_meta", rebuildMetaSchema, t => t.ExcludeFromMigrations());
            meta.Property(m => m.ProcessorId).HasColumnName("processor_id");
            meta.Property(m => m.ActiveVersion).HasColumnName("active_version");
        });

        var entity = modelBuilder.Entity<TEntity>();
        ConfigureVersionedKey(entity);

        // EF rewrites a DbContext.Set<T>() call inside a query filter into a query root, so the
        // context instance is never dereferenced; it only has to be an expression of DbContext
        // type. The subquery runs per query, never cached: a promotion must be seen immediately,
        // and pooled contexts outlive it.
        DbContext context = null!;
        entity.HasQueryFilter(ProjectionQueryFilters.ActiveVersion, e =>
            e.RebuildVersion == (context.Set<ProjectionRebuildMetaRow>()
                .Where(m => m.ProcessorId == processorId)
                .Select(m => (int?)m.ActiveVersion)
                .FirstOrDefault() ?? ProjectionVersions.Initial));

        configure?.Invoke(entity);

        return modelBuilder;
    }

    private static void ConfigureVersionedKey<TEntity>(EntityTypeBuilder<TEntity> entity)
        where TEntity : class, IProjectionEntity
    {
        entity.HasKey(e => new { e.DocumentId, e.RebuildVersion });
        entity.Property(e => e.RebuildVersion).HasDefaultValue(ProjectionVersions.Initial);

        // The live version is the only one anything queries by document id, and a rebuild
        // doubles the row count while it runs, so the version leads the index.
        entity.HasIndex(e => new { e.RebuildVersion, e.UpdatedAt });
    }
}
