using System.Data;
using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Dcb.EntityFramework.Inline;

/// <summary>
/// Inline EF projection driven by a <see cref="ProjectionDeclaration{TEntity}"/>.
/// Runs immediately after <see cref="IEventStore.AppendAsync"/> commits, providing
/// read-your-writes consistency at the cost of coupling projection latency and failure
/// to the originating mutation.
/// </summary>
/// <remarks>
/// Idempotency is enforced through <see cref="IProjectionEntity.LastProcessedPosition"/>:
/// events with a global position less than or equal to the stored position are skipped.
/// This makes it safe for the same projection to also be processed by an async path during
/// a migration window — the row whose position is highest wins.
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

    public DeclaredEfInlineProjection(
        ProjectionDeclaration<TEntity> declaration,
        IDbContextFactory<TDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(contextFactory);
        _declaration = declaration;
        _contextFactory = contextFactory;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _declaration.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        // Build doc-id map up front so we can load all affected rows in one query.
        var docIdMap = new Dictionary<IEventEnvelope, string>(ReferenceEqualityComparer.Instance);
        foreach (var evt in events)
        {
            if (!_declaration.HandledEventTypes.Contains(evt.EventType.Id))
                continue;

            var docId = _declaration.GetDocumentId(evt);
            if (docId is not null)
                docIdMap[evt] = docId;
        }

        if (docIdMap.Count == 0)
            return;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Join the append transaction if one was provided. Today the event store always
        // passes null — inline runs in its own transaction directly after the append commits.
        if (transaction is System.Data.Common.DbTransaction dbTransaction)
        {
            await context.Database.UseTransactionAsync(dbTransaction, ct);
        }

        var documentKeys = docIdMap.Values.Distinct().ToList();
        var existing = await context.Set<TEntity>()
            .Where(e => documentKeys.Contains(e.DocumentId))
            .ToDictionaryAsync(e => e.DocumentId, ct);

        var pending = new Dictionary<string, TEntity>();
        var deletes = new HashSet<string>();

        foreach (var evt in events)
        {
            if (!docIdMap.TryGetValue(evt, out var docId))
                continue;

            TEntity state;
            if (pending.TryGetValue(docId, out var pendingState))
                state = pendingState;
            else if (deletes.Contains(docId))
                state = _declaration.InitialState();
            else
                state = existing.GetValueOrDefault(docId) ?? _declaration.InitialState();

            // Idempotency guard: skip events we've already processed for this entity.
            if (state.LastProcessedPosition >= evt.GlobalPosition)
                continue;

            var ctx = ProjectionContext.FromEnvelope(evt);
            var result = _declaration.Apply(state, evt, ctx);

            switch (result)
            {
                case ProjectionResult<TEntity>.Set set:
                    set.State.LastProcessedPosition = evt.GlobalPosition;
                    pending[docId] = set.State;
                    deletes.Remove(docId);
                    break;

                case ProjectionResult<TEntity>.Delete:
                    deletes.Add(docId);
                    pending.Remove(docId);
                    break;
            }
        }

        if (pending.Count == 0 && deletes.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;

        foreach (var docId in deletes)
        {
            if (existing.TryGetValue(docId, out var toDelete))
                context.Set<TEntity>().Remove(toDelete);
        }

        foreach (var (docId, entity) in pending)
        {
            entity.DocumentId = docId;
            entity.UpdatedAt = now;

            if (existing.ContainsKey(docId))
                context.Set<TEntity>().Update(entity);
            else
                context.Set<TEntity>().Add(entity);
        }

        await context.SaveChangesAsync(ct);
    }
}
