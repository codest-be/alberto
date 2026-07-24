using System.Data.Common;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Entity Framework implementation of <see cref="IStateStore{TState}"/>.
/// Stores entities with proper relational columns instead of JSONB.
/// Each operation creates a fresh DbContext for thread safety with parallel projections.
/// </summary>
/// <typeparam name="TEntity">The entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
public sealed class EfStateStore<TEntity, TDbContext> : IStateStore<TEntity>, IAsyncDisposable
    where TEntity : class, IProjectionEntity, new()
    where TDbContext : DbContext
{
    // ANSI SQLSTATE "23505" is Postgres' unique_violation. Same constant as the inline projections.
    private const string UniqueViolationSqlState = "23505";

    private readonly IDbContextFactory<TDbContext> _contextFactory;

    /// <summary>
    /// Creates a new EF state store.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    public EfStateStore(IDbContextFactory<TDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, TEntity>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct = default)
    {
        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TEntity>();

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entities = await context.Set<TEntity>()
            .AsNoTracking()
            .Where(e => ids.Contains(e.DocumentId))
            .ToListAsync(ct);

        return entities.ToDictionary(e => e.DocumentId);
    }

    /// <inheritdoc/>
    public async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct = default)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var allDocIds = upserts.Keys.Concat(deletes).Distinct().ToList();
        var existingEntities = await context.Set<TEntity>()
            .Where(e => allDocIds.Contains(e.DocumentId))
            .ToDictionaryAsync(e => e.DocumentId, ct);

        foreach (var docId in deletes)
        {
            if (existingEntities.TryGetValue(docId, out var existing))
                context.Set<TEntity>().Remove(existing);
        }

        foreach (var (docId, newEntity) in upserts)
        {
            newEntity.DocumentId = docId;
            newEntity.UpdatedAt = DateTimeOffset.UtcNow;

            if (existingEntities.TryGetValue(docId, out var existing))
            {
                context.Entry(existing).CurrentValues.SetValues(newEntity);
                CopyOwnedNavigations(context.Entry(existing), newEntity);
            }
            else
            {
                context.Set<TEntity>().Add(newEntity);
            }
        }

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            AlbertoMetrics.ConcurrencyConflicts.Add(1);
            var conflictedEntry = ex.Entries.FirstOrDefault();
            var conflictedDocId = (conflictedEntry?.Entity as IProjectionEntity)?.DocumentId ?? "unknown";
            throw new ConcurrencyConflictException(conflictedDocId, ex);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyViolation(ex))
        {
            AlbertoMetrics.ConcurrencyConflicts.Add(1);
            await RetryWithBackoffAsync(upserts, deletes, ct);
        }
    }

    private async Task RetryWithBackoffAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct)
    {
        const int maxRetries = 5;
        var delay = TimeSpan.FromMilliseconds(50);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);

            foreach (var docId in deletes)
            {
                var existing = await context.Set<TEntity>()
                    .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);
                if (existing != null)
                    context.Set<TEntity>().Remove(existing);
            }

            foreach (var (docId, newEntity) in upserts)
            {
                newEntity.DocumentId = docId;
                newEntity.UpdatedAt = DateTimeOffset.UtcNow;

                var existing = await context.Set<TEntity>()
                    .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);

                if (existing != null)
                {
                    context.Entry(existing).CurrentValues.SetValues(newEntity);
                    CopyOwnedNavigations(context.Entry(existing), newEntity);
                }
                else
                {
                    context.Set<TEntity>().Add(newEntity);
                }
            }

            try
            {
                await context.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyViolation(ex))
            {
                if (attempt >= maxRetries)
                {
                    await SaveEntitiesOneByOneAsync(upserts, deletes, ct);
                    return;
                }

                AlbertoMetrics.ConcurrencyConflicts.Add(1);
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }
    }

    private async Task SaveEntitiesOneByOneAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct)
    {
        foreach (var (docId, newEntity) in upserts)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync(ct);

                newEntity.DocumentId = docId;
                newEntity.UpdatedAt = DateTimeOffset.UtcNow;

                var existing = await context.Set<TEntity>()
                    .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);

                if (existing != null)
                {
                    context.Entry(existing).CurrentValues.SetValues(newEntity);
                    CopyOwnedNavigations(context.Entry(existing), newEntity);
                }
                else
                {
                    context.Set<TEntity>().Add(newEntity);
                }

                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyViolation(ex))
            {
                AlbertoMetrics.ConcurrencyConflicts.Add(1);

                try
                {
                    await using var retryContext = await _contextFactory.CreateDbContextAsync(ct);

                    var nowExisting = await retryContext.Set<TEntity>()
                        .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);

                    if (nowExisting != null)
                    {
                        retryContext.Entry(nowExisting).CurrentValues.SetValues(newEntity);
                        CopyOwnedNavigations(retryContext.Entry(nowExisting), newEntity);
                        await retryContext.SaveChangesAsync(ct);
                    }
                }
                catch (DbUpdateException)
                {
                    // Give up on this entity.
                }
            }
        }
    }

    /// <summary>
    /// Copies owned-navigation values from <paramref name="source"/> onto a tracked entry in
    /// one reflection pass. Extracted to eliminate the identical three-line loop that previously
    /// appeared four times across ApplyChangesAsync, RetryWithBackoffAsync, and
    /// SaveEntitiesOneByOneAsync (twice).
    /// </summary>
    private static void CopyOwnedNavigations(EntityEntry<TEntity> trackedEntry, TEntity source)
    {
        foreach (var nav in trackedEntry.Navigations
                     .Where(n => n.Metadata.TargetEntityType.IsOwned()))
        {
            var propInfo = typeof(TEntity).GetProperty(nav.Metadata.Name);
            if (propInfo != null)
                nav.CurrentValue = propInfo.GetValue(source);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the exception is a duplicate-key (unique-constraint)
    /// violation, using the same <c>DbException.SqlState</c> pattern as the inline projections
    /// instead of reflection-based or message-string matching.
    /// </summary>
    private static bool IsDuplicateKeyViolation(DbUpdateException ex) =>
        ex.InnerException is DbException { SqlState: UniqueViolationSqlState };

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
