using System.Data;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Telemetry;
using Microsoft.EntityFrameworkCore;

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
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TEntity>();

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
            await UseTransactionAsync(context, transaction, ct);

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
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
            await UseTransactionAsync(context, transaction, ct);

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

                foreach (var ownedNav in context.Entry(existing).Navigations
                    .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                {
                    var propName = ownedNav.Metadata.Name;
                    var propInfo = typeof(TEntity).GetProperty(propName);
                    if (propInfo != null)
                        ownedNav.CurrentValue = propInfo.GetValue(newEntity);
                }
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
            await RetryWithBackoffAsync(upserts, deletes, transaction, ct);
        }
    }

    private async Task RetryWithBackoffAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        IDbTransaction? transaction,
        CancellationToken ct)
    {
        const int maxRetries = 5;
        var delay = TimeSpan.FromMilliseconds(50);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);

            if (transaction != null)
                await UseTransactionAsync(context, transaction, ct);

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

                    foreach (var ownedNav in context.Entry(existing).Navigations
                        .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                    {
                        var propName = ownedNav.Metadata.Name;
                        var propInfo = typeof(TEntity).GetProperty(propName);
                        if (propInfo != null)
                            ownedNav.CurrentValue = propInfo.GetValue(newEntity);
                    }
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
                    await SaveEntitiesOneByOneAsync(upserts, deletes, transaction, ct);
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
        IDbTransaction? transaction,
        CancellationToken ct)
    {
        foreach (var (docId, newEntity) in upserts)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync(ct);

                if (transaction != null)
                    await UseTransactionAsync(context, transaction, ct);

                newEntity.DocumentId = docId;
                newEntity.UpdatedAt = DateTimeOffset.UtcNow;

                var existing = await context.Set<TEntity>()
                    .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);

                if (existing != null)
                {
                    context.Entry(existing).CurrentValues.SetValues(newEntity);
                    foreach (var ownedNav in context.Entry(existing).Navigations
                        .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                    {
                        var propName = ownedNav.Metadata.Name;
                        var propInfo = typeof(TEntity).GetProperty(propName);
                        if (propInfo != null)
                            ownedNav.CurrentValue = propInfo.GetValue(newEntity);
                    }
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

                    if (transaction != null)
                        await UseTransactionAsync(retryContext, transaction, ct);

                    var nowExisting = await retryContext.Set<TEntity>()
                        .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);

                    if (nowExisting != null)
                    {
                        retryContext.Entry(nowExisting).CurrentValues.SetValues(newEntity);
                        foreach (var ownedNav in retryContext.Entry(nowExisting).Navigations
                            .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                        {
                            var propName = ownedNav.Metadata.Name;
                            var propInfo = typeof(TEntity).GetProperty(propName);
                            if (propInfo != null)
                                ownedNav.CurrentValue = propInfo.GetValue(newEntity);
                        }
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TEntity>> ListRecentAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.Set<TEntity>()
            .AsNoTracking()
            .OrderByDescending(e => e.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    private static async Task UseTransactionAsync(TDbContext context, IDbTransaction transaction, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction == null)
        {
            var dbTransaction = transaction as System.Data.Common.DbTransaction;
            if (dbTransaction != null)
                await context.Database.UseTransactionAsync(dbTransaction, ct);
        }
    }

    private static bool IsDuplicateKeyViolation(DbUpdateException ex)
    {
        var currentEx = ex as Exception;
        while (currentEx != null)
        {
            var sqlStateProp = currentEx.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(currentEx) is string sqlState && sqlState == "23505")
                return true;

            if (currentEx.Data.Contains("SqlState") && currentEx.Data["SqlState"]?.ToString() == "23505")
                return true;

            if (currentEx.Message.Contains("23505") ||
                currentEx.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                currentEx.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
                return true;

            currentEx = currentEx.InnerException;
        }

        return false;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
