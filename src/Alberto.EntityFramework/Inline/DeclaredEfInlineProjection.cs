using System.Data.Common;
using Alberto.Projections;
using Microsoft.EntityFrameworkCore;

namespace Alberto.EntityFramework.Inline;

/// <summary>
/// Inline EF projection driven by a <see cref="ProjectionDeclaration{TEntity}"/>.
/// Runs inside <see cref="IEventStore.AppendAsync"/> (<see cref="ProcessAsync"/>), on the append's own
/// transaction, providing read-your-writes consistency at the cost of coupling projection latency and
/// failure to the originating mutation.
/// </summary>
/// <remarks>
/// Idempotency is enforced through <see cref="IProjectionEntity.LastProcessedPosition"/>:
/// events with a global position less than or equal to the stored position are skipped.
/// This makes it safe for the same projection to also be processed by an async path during
/// a migration window — the row whose position is highest wins.
/// <para>
/// Rebuild versions: every read is filtered by <see cref="IProjectionEntity.RebuildVersion"/>
/// and every write stamps it, exactly as <c>EfStateStore</c> does. This matters precisely
/// because of the migration window above — register the same entity as both inline and async,
/// start a rebuild of the async side, and the table holds <c>(docId, v1)</c> and
/// <c>(docId, v2)</c> at once. An unfiltered load returns both and
/// <c>ToDictionaryAsync(e =&gt; e.DocumentId)</c> throws a duplicate-key
/// <see cref="ArgumentException"/> — which, on the inline path, surfaces out of the caller's
/// <see cref="IEventStore.AppendAsync"/> for the whole rebuild window.
/// </para>
/// <para>
/// Multi-tenant note: <see cref="IProjectionEntity"/> has no tenant column, and the load
/// query filters only by <see cref="IProjectionEntity.DocumentId"/>. This matches the
/// async <c>EfStateStore</c> path. If a projection's storage is shared across tenants,
/// the declaration's <c>GetDocumentId</c> must encode tenant identity into the document id
/// (for example by prefixing it with the envelope's <c>TenantId</c>) to avoid collisions.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The projection entity type.</typeparam>
/// <typeparam name="TDbContext">The EF DbContext containing the entity DbSet.</typeparam>
internal sealed class DeclaredEfInlineProjection<TEntity, TDbContext> : IInlineProjection
    where TEntity : class, IProjectionEntity, new()
    where TDbContext : DbContext
{
    private readonly ProjectionDeclaration<TEntity> _declaration;
    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly ProjectionVersion _rebuildVersion;

    /// <param name="declaration">The projection declaration.</param>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    /// <param name="rebuildVersion">
    /// A live handle on the version to read and write. Its
    /// <see cref="ProjectionVersion.Current"/> is resolved per attempt rather than cached, for
    /// the same reason as <c>EfStateStore</c>: a promotion moves it underneath a long-lived
    /// projection. Omit for a projection whose entity is never rebuilt; it then resolves to
    /// version 1 forever.
    /// </param>
    public DeclaredEfInlineProjection(
        ProjectionDeclaration<TEntity> declaration,
        IDbContextFactory<TDbContext> contextFactory,
        ProjectionVersion rebuildVersion = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(contextFactory);
        _declaration = declaration;
        _contextFactory = contextFactory;
        _rebuildVersion = rebuildVersion;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _declaration.HandledEventTypes;

    /// <inheritdoc/>
    /// <remarks>
    /// Writes on the append's own connection and transaction, so the row commits or rolls back with
    /// the events and is written in log order under the append lock. There is no concurrency retry
    /// here: a failed statement aborts the transaction, so a failure throws, the append rolls back and
    /// the caller retries the command. The DbContext must be in the events' database and must not use
    /// a retrying execution strategy, which EF refuses to combine with a transaction it did not start.
    /// When the backend has no database transaction (<see cref="AppendTransaction.None"/>, the in-memory
    /// backend) the projection writes through a context of its own, still under the backend's append lock
    /// and before the events become visible, so a failure still aborts the append.
    /// </remarks>
    public async Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        AppendTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var batch = new ProjectionBatch<TEntity>(_declaration, events, serializer: null);
        var documentKeys = batch.DocumentIds;

        if (documentKeys.Count == 0)
            return;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (transaction.Connection is { } connection)
        {
            GuardJoinable(context, connection);
            context.Database.SetDbConnection(connection);
            await context.Database.UseTransactionAsync(transaction.Transaction!, cancellationToken);
        }

        await ApplyAsync(context, batch, documentKeys, cancellationToken);
    }

    private void GuardJoinable(TDbContext context, DbConnection connection)
    {
        var own = context.Database.GetDbConnection();
        if (!string.Equals(own.DataSource, connection.DataSource, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(own.Database, connection.Database, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Inline EF projection '{_declaration.ProcessorId}' is not in the event store's database " +
                $"('{own.DataSource}/{own.Database}' versus '{connection.DataSource}/{connection.Database}'), " +
                "so it cannot be written in the append's transaction. Point its DbContext at the event store's " +
                "database, or register the projection as ProjectionMode.Async.");
        }

        if (context.Database.CreateExecutionStrategy().RetriesOnFailure)
        {
            throw new InvalidOperationException(
                $"Inline EF projection '{_declaration.ProcessorId}' runs in the append's transaction, which " +
                "EF refuses to combine with a retrying execution strategy. Remove EnableRetryOnFailure from " +
                "the DbContext this projection uses.");
        }
    }

    private async Task ApplyAsync(
        TDbContext context,
        ProjectionBatch<TEntity> batch,
        IReadOnlyList<string> documentKeys,
        CancellationToken cancellationToken)
    {
        // Resolved once for the whole attempt, so a promotion landing mid-write cannot split
        // the load, the upserts and the deletes across two versions.
        var version = _rebuildVersion.Current;

        var outcome = await EfProjectionWriter.LoadFoldSaveAsync<TEntity>(
            context,
            version,
            documentKeys,
            existing =>
            {
                var changes = batch.Fold(existing);
                return (changes.Upserts, changes.Deletes);
            },
            cancellationToken);

        if (outcome is { Saved: false, NothingToWrite: false })
        {
            context.ChangeTracker.Clear();
            await EfProjectionWriter.WriteAsync(context, version, outcome.Upserts, outcome.Deletes, cancellationToken);
        }
    }
}
