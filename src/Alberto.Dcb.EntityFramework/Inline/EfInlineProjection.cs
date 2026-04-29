using System.Data;
using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;

#pragma warning disable CS0618 // Obsolete projection types used intentionally for backward-compatibility
namespace Alberto.Dcb.EntityFramework.Inline;

/// <summary>
/// Inline projection that uses Entity Framework for storage.
/// Runs within the event append transaction for strong consistency.
/// </summary>
/// <typeparam name="TEntity">The EF entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TProjection">The projection type.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
internal sealed class EfInlineProjection<TEntity, TProjection, TDbContext> : IInlineProjection
    where TEntity : class, IProjectionEntity, new()
    where TProjection : Projection<TEntity>, new()
    where TDbContext : DbContext
{
    private readonly TProjection _projection = new();
    private readonly IDbContextFactory<TDbContext> _contextFactory;

    /// <summary>
    /// Creates a new inline EF projection.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    public EfInlineProjection(IDbContextFactory<TDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        // Filter to events we handle and group by document ID
        var byDocument = events
            .Where(e => HandledEventTypes.Contains(e.EventType.Id))
            .GroupBy(e => _projection.GetDocumentId(e) ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (byDocument.Count == 0)
            return;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Join the transaction if provided
        if (transaction is System.Data.Common.DbTransaction dbTransaction)
        {
            await context.Database.UseTransactionAsync(dbTransaction, ct);
        }

        // Load current state for all affected documents
        var documentKeys = byDocument.Keys.ToList();
        var existingEntities = await context.Set<TEntity>()
            .Where(e => documentKeys.Contains(e.DocumentId))
            .ToDictionaryAsync(e => e.DocumentId, ct);

        // Process events for each document
        foreach (var (key, docEvents) in byDocument)
        {
            existingEntities.TryGetValue(key, out var entity);
            entity ??= new TEntity { DocumentId = key };

            // Fold all events for this document
            ProjectionResult<TEntity> result = ProjectionResults.Unchanged<TEntity>();
            foreach (var envelope in docEvents)
            {
                // Get state from previous result if it was a Set
                entity = result switch
                {
                    ProjectionResult<TEntity>.Set s => s.State,
                    _ => entity
                };
                result = _projection.Apply(entity, envelope);
            }

            // Apply the final result
            switch (result)
            {
                case ProjectionResult<TEntity>.Set s:
                    var finalEntity = s.State;
                    finalEntity.DocumentId = key;
                    finalEntity.UpdatedAt = DateTimeOffset.UtcNow;

                    if (existingEntities.ContainsKey(key))
                    {
                        context.Set<TEntity>().Update(finalEntity);
                    }
                    else
                    {
                        context.Set<TEntity>().Add(finalEntity);
                    }
                    break;

                case ProjectionResult<TEntity>.Delete:
                    if (existingEntities.TryGetValue(key, out var toDelete))
                    {
                        context.Set<TEntity>().Remove(toDelete);
                    }
                    break;
            }
        }

        await context.SaveChangesAsync(ct);
    }
}
