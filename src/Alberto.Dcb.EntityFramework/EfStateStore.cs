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
    private readonly string _tenantId;

    /// <summary>
    /// Creates a new EF state store.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    /// <param name="tenantId">The tenant ID for this store instance.</param>
    public EfStateStore(IDbContextFactory<TDbContext> contextFactory, string tenantId)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
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

        // Create fresh context for this operation
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
        {
            await UseTransactionAsync(context, transaction, ct);
        }

        // Load without tracking since we're just reading
        var entities = await context.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.TenantId == _tenantId && ids.Contains(e.DocumentId))
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

        // Create fresh context for this operation
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
        {
            await UseTransactionAsync(context, transaction, ct);
        }

        // Load all entities we need to update or delete in one query (efficient)
        var allDocIds = upserts.Keys.Concat(deletes).Distinct().ToList();
        var existingEntities = await context.Set<TEntity>()
            .Where(e => e.TenantId == _tenantId && allDocIds.Contains(e.DocumentId))
            .ToDictionaryAsync(e => e.DocumentId, ct);

        // Handle deletes
        foreach (var docId in deletes)
        {
            if (existingEntities.TryGetValue(docId, out var existing))
            {
                context.Set<TEntity>().Remove(existing);
            }
        }

        // Handle upserts - EF Core will batch these efficiently
        foreach (var (docId, newEntity) in upserts)
        {
            newEntity.TenantId = _tenantId;
            newEntity.DocumentId = docId;
            newEntity.UpdatedAt = DateTimeOffset.UtcNow;

            if (existingEntities.TryGetValue(docId, out var existing))
            {
                // Update existing entity - copy scalar properties
                context.Entry(existing).CurrentValues.SetValues(newEntity);

                // Handle owned collections (JSON) by copying via reflection
                foreach (var ownedNav in context.Entry(existing).Navigations
                    .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                {
                    var propName = ownedNav.Metadata.Name;
                    var propInfo = typeof(TEntity).GetProperty(propName);
                    if (propInfo != null)
                    {
                        var newValue = propInfo.GetValue(newEntity);
                        ownedNav.CurrentValue = newValue;
                    }
                }
            }
            else
            {
                // Insert new entity
                context.Set<TEntity>().Add(newEntity);
            }
        }

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Record concurrency conflict metric
            AlbertoMetrics.ConcurrencyConflicts.Add(1);

            // Find the document ID that caused the conflict
            var conflictedEntry = ex.Entries.FirstOrDefault();
            var conflictedDocId = (conflictedEntry?.Entity as IProjectionEntity)?.DocumentId ?? "unknown";

            throw new ConcurrencyConflictException(conflictedDocId, ex);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyViolation(ex))
        {
            // Another instance already inserted some entities - this shouldn't happen
            // with proper tenant distribution, but handle it gracefully by retrying
            AlbertoMetrics.ConcurrencyConflicts.Add(1);

            // Retry with fresh context - entities should exist now
            await RetryAfterDuplicateKeyAsync(upserts, deletes, transaction, ct);
        }
    }

    /// <summary>
    /// Retry after duplicate key error. Reload entities and update instead of insert.
    /// </summary>
    private async Task RetryAfterDuplicateKeyAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        IDbTransaction? transaction,
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
        {
            await UseTransactionAsync(context, transaction, ct);
        }

        // Reload all entities - some should exist now (inserted by other instance)
        var allDocIds = upserts.Keys.Concat(deletes).Distinct().ToList();
        var existingEntities = await context.Set<TEntity>()
            .Where(e => e.TenantId == _tenantId && allDocIds.Contains(e.DocumentId))
            .ToDictionaryAsync(e => e.DocumentId, ct);

        // Handle deletes
        foreach (var docId in deletes)
        {
            if (existingEntities.TryGetValue(docId, out var existing))
            {
                context.Set<TEntity>().Remove(existing);
            }
        }

        // Handle upserts - now most should be updates
        foreach (var (docId, newEntity) in upserts)
        {
            newEntity.TenantId = _tenantId;
            newEntity.DocumentId = docId;
            newEntity.UpdatedAt = DateTimeOffset.UtcNow;

            if (existingEntities.TryGetValue(docId, out var existing))
            {
                // Update existing entity
                context.Entry(existing).CurrentValues.SetValues(newEntity);

                foreach (var ownedNav in context.Entry(existing).Navigations
                    .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                {
                    var propName = ownedNav.Metadata.Name;
                    var propInfo = typeof(TEntity).GetProperty(propName);
                    if (propInfo != null)
                    {
                        var newValue = propInfo.GetValue(newEntity);
                        ownedNav.CurrentValue = newValue;
                    }
                }
            }
            else
            {
                // Still doesn't exist - try insert again
                context.Set<TEntity>().Add(newEntity);
            }
        }

        // If this still fails, let it propagate - something is seriously wrong
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TEntity>> ListRecentAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .OrderByDescending(e => e.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    private static async Task UseTransactionAsync(TDbContext context, IDbTransaction transaction, CancellationToken ct)
    {
        // EF Core can use an existing DbTransaction if it's the right type
        if (context.Database.CurrentTransaction == null)
        {
            var dbTransaction = transaction as System.Data.Common.DbTransaction;
            if (dbTransaction != null)
            {
                await context.Database.UseTransactionAsync(dbTransaction, ct);
            }
        }
    }

    private static bool IsDuplicateKeyViolation(DbUpdateException ex)
    {
        // Check for PostgreSQL unique_violation (23505) or SQL Server duplicate key (2601, 2627)
        // Walk the entire exception chain to find PostgresException
        var currentEx = ex as Exception;
        while (currentEx != null)
        {
            // Try to get SqlState property (PostgreSQL)
            var sqlStateProp = currentEx.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(currentEx) is string sqlState && sqlState == "23505")
                return true;

            // Check Exception.Data for SqlState (sometimes stored there)
            if (currentEx.Data.Contains("SqlState") && currentEx.Data["SqlState"]?.ToString() == "23505")
                return true;

            // Check message
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
        // Nothing to dispose - contexts are created and disposed per-operation
        return ValueTask.CompletedTask;
    }
}
