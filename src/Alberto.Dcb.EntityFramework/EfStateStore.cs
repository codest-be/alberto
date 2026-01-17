using System.Data;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Entity Framework implementation of <see cref="IStateStore{TState}"/>.
/// Stores entities with proper relational columns instead of JSONB.
/// Caches the DbContext between LoadManyAsync and ApplyChangesAsync calls
/// to benefit from change tracking and avoid redundant loads.
/// </summary>
/// <typeparam name="TEntity">The entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
public sealed class EfStateStore<TEntity, TDbContext> : IStateStore<TEntity>, IAsyncDisposable
    where TEntity : class, IProjectionEntity, new()
    where TDbContext : DbContext
{
    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly string _tenantId;
    private TDbContext? _context;
    private readonly Dictionary<string, TEntity> _loadedEntities = new();

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

        // Reuse or create context
        _context ??= await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
        {
            await UseTransactionAsync(_context, transaction, ct);
        }

        // Load with tracking so we can update in ApplyChangesAsync
        var entities = await _context.Set<TEntity>()
            .Where(e => e.TenantId == _tenantId && ids.Contains(e.DocumentId))
            .ToListAsync(ct);

        var result = entities.ToDictionary(e => e.DocumentId);

        // Cache for ApplyChangesAsync
        foreach (var (docId, entity) in result)
            _loadedEntities[docId] = entity;

        return result;
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

        _context ??= await _contextFactory.CreateDbContextAsync(ct);

        if (transaction != null)
        {
            await UseTransactionAsync(_context, transaction, ct);
        }

        // Handle deletes - use cached tracked entities
        foreach (var docId in deletes)
        {
            if (_loadedEntities.TryGetValue(docId, out var existing))
            {
                _context.Set<TEntity>().Remove(existing);
                _loadedEntities.Remove(docId);
            }
        }

        // Handle upserts - use cached tracked entities
        foreach (var (docId, newEntity) in upserts)
        {
            newEntity.TenantId = _tenantId;
            newEntity.DocumentId = docId;
            newEntity.UpdatedAt = DateTimeOffset.UtcNow;

            if (_loadedEntities.TryGetValue(docId, out var existing))
            {
                // Update all properties including owned types (JSON columns)
                _context.Entry(existing).CurrentValues.SetValues(newEntity);

                // For owned collections (JSON), we need to replace the entire collection
                foreach (var ownedNav in _context.Entry(existing).Navigations
                    .Where(n => n.Metadata.TargetEntityType.IsOwned()))
                {
                    var newValue = _context.Entry(newEntity).Navigation(ownedNav.Metadata.Name).CurrentValue;
                    ownedNav.CurrentValue = newValue;
                }
            }
            else
            {
                // Insert new entity
                _context.Set<TEntity>().Add(newEntity);
            }
        }

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Record concurrency conflict metric
            AlbertoMetrics.ConcurrencyConflicts.Add(1);

            // Find the document ID that caused the conflict
            var conflictedEntry = ex.Entries.FirstOrDefault();
            var conflictedDocId = (conflictedEntry?.Entity as IProjectionEntity)?.DocumentId ?? "unknown";

            // Reset for retry
            _loadedEntities.Clear();
            await _context.DisposeAsync();
            _context = null;

            throw new ConcurrencyConflictException(conflictedDocId, ex);
        }

        // Reset for next event
        _loadedEntities.Clear();
        await _context.DisposeAsync();
        _context = null;
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            await _context.DisposeAsync();
            _context = null;
        }
        _loadedEntities.Clear();
    }
}
