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
/// <remarks>
/// Every read is filtered by <see cref="IProjectionEntity.RebuildVersion"/> and every write
/// stamps it, so a shadow rebuild loop's rows coexist with the live ones in the same table
/// instead of overwriting them. That only holds if the entity's key includes the column —
/// see <see cref="EfProjectionModelBuilderExtensions.ProjectionEntity{TEntity}"/>.
/// </remarks>
/// <typeparam name="TEntity">The entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
public sealed class EfStateStore<TEntity, TDbContext> : IStateStore<TEntity>, IAsyncDisposable
    where TEntity : class, IProjectionEntity, new()
    where TDbContext : DbContext
{
    // ANSI SQLSTATE "23505" is Postgres' unique_violation. Same constant as the inline projections.
    private const string UniqueViolationSqlState = "23505";
    private const int MaxWriteAttempts = 5;

    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly Func<int> _rebuildVersion;

    /// <summary>
    /// Creates a new EF state store.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    /// <param name="rebuildVersion">
    /// Resolves the version to read and write, on every operation rather than once, because a
    /// promotion moves it underneath a long-lived store. Omit for a projection that is never
    /// rebuilt; it then resolves to version 1 forever.
    /// </param>
    public EfStateStore(IDbContextFactory<TDbContext> contextFactory, Func<int>? rebuildVersion = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _rebuildVersion = rebuildVersion ?? ProjectionVersions.NeverRebuilt;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, TEntity>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct = default)
    {
        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TEntity>();

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var version = _rebuildVersion();
        var entities = await context.Set<TEntity>()
            .AsNoTracking()
            .Where(e => ids.Contains(e.DocumentId) && e.RebuildVersion == version)
            .ToListAsync(ct);

        return entities.ToDictionary(e => e.DocumentId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Ordered by <see cref="IProjectionEntity.UpdatedAt"/>, which
    /// <see cref="WriteBatchOnceAsync"/> stamps on every upsert. That is the same column the
    /// JSONB store orders by, so the two adapters agree on what "recent" means.
    /// </remarks>
    public async Task<IReadOnlyList<TEntity>> ListRecentAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var version = _rebuildVersion();
        return await context.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.RebuildVersion == version)
            .OrderByDescending(e => e.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct = default)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        // Resolved once for the whole batch, so a promotion landing mid-batch cannot split
        // attempts, upserts, and deletes across two versions.
        var version = _rebuildVersion();
        var delay = TimeSpan.FromMilliseconds(50);

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                await WriteBatchOnceAsync(upserts, deletes, version, ct);
                return;
            }
            catch (Exception ex) when (IsRetryableConflict(ex))
            {
                AlbertoMetrics.ConcurrencyConflicts.Add(1);

                if (attempt == MaxWriteAttempts)
                    throw ToConcurrencyConflict(ex, upserts.Keys, deletes);

                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }
    }

    /// <summary>
    /// Applies the complete change set in one fresh context. Each retry calls this method again,
    /// so no tracked state or partial fallback path can leak between attempts.
    /// </summary>
    private async Task WriteBatchOnceAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        int version,
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var allDocIds = upserts.Keys.Concat(deletes).Distinct().ToList();
        var existingEntities = await context.Set<TEntity>()
            .Where(e => allDocIds.Contains(e.DocumentId) && e.RebuildVersion == version)
            .ToDictionaryAsync(e => e.DocumentId, ct);

        foreach (var docId in deletes)
        {
            if (existingEntities.TryGetValue(docId, out var existing))
                context.Set<TEntity>().Remove(existing);
        }

        foreach (var (docId, newEntity) in upserts)
        {
            newEntity.DocumentId = docId;
            newEntity.RebuildVersion = version;
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

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Copies owned-navigation values from <paramref name="source"/> onto a tracked entry in
    /// one reflection pass.
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
    /// Returns <see langword="true"/> when the escaped write failure is retryable.
    /// Both optimistic concurrency and duplicate-key races take the same atomic retry path.
    /// </summary>
    private static bool IsRetryableConflict(Exception ex) =>
        ex is DbUpdateConcurrencyException ||
        ex is DbUpdateException && HasSqlState(ex, UniqueViolationSqlState);

    private static bool HasSqlState(Exception ex, string sqlState)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: var state } &&
                string.Equals(state, sqlState, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ConcurrencyConflictException ToConcurrencyConflict(
        Exception ex,
        IEnumerable<string> upsertIds,
        IEnumerable<string> deleteIds)
    {
        var entryId = (ex as DbUpdateConcurrencyException)?.Entries
            .Select(entry => (entry.Entity as IProjectionEntity)?.DocumentId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var documentId = entryId
            ?? upsertIds.FirstOrDefault()
            ?? deleteIds.FirstOrDefault()
            ?? "unknown";

        return new ConcurrencyConflictException(documentId, ex);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
