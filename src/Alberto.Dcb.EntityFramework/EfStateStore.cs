using System.Data;
using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Entity Framework implementation of <see cref="IStateStore{TState}"/>.
/// Stores entities with proper relational columns instead of JSONB.
/// </summary>
/// <typeparam name="TEntity">The entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
public sealed class EfStateStore<TEntity, TDbContext> : IStateStore<TEntity>
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

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
        {
            await UseTransactionAsync(context, transaction, ct);
        }

        // Load with tracking so we can update in ApplyChangesAsync
        var entities = await context.Set<TEntity>()
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

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
        {
            await UseTransactionAsync(context, transaction, ct);
        }

        // Load existing entities with tracking
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

        // Handle upserts
        foreach (var (docId, newEntity) in upserts)
        {
            newEntity.TenantId = _tenantId;
            newEntity.DocumentId = docId;
            newEntity.UpdatedAt = DateTimeOffset.UtcNow;

            if (existingEntities.TryGetValue(docId, out var existing))
            {
                // Update all properties including owned types (JSON columns)
                context.Entry(existing).CurrentValues.SetValues(newEntity);

                // For owned collections (JSON), we need to replace the entire collection
                foreach (var ownedNav in context.Entry(existing).Navigations
                    .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                {
                    var newValue = context.Entry(newEntity).Navigation(ownedNav.Metadata.Name).CurrentValue;
                    ownedNav.CurrentValue = newValue;
                }
            }
            else
            {
                // Insert new entity
                context.Set<TEntity>().Add(newEntity);
            }
        }

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
}
