using System.Data.Common;
using Alberto.Projections;
using Alberto.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Alberto.EntityFramework;

/// <summary>
/// Entity Framework implementation of <see cref="IStateStore{TState}"/>.
/// Stores entities with proper relational columns instead of JSONB.
/// Each operation creates a fresh DbContext for thread safety with parallel projections.
/// </summary>
/// <remarks>
/// Every read is filtered by <see cref="IProjectionEntity.RebuildVersion"/> and every write
/// stamps it, so a shadow rebuild loop's rows coexist with the live ones in the same table
/// instead of overwriting them. That only holds if the entity's key includes the column —
/// see <see cref="EfProjectionModelBuilderExtensions"/>. Every query here also turns off
/// <see cref="ProjectionQueryFilters.ActiveVersion"/>, because the store addresses a version
/// explicitly: a shadow loop filtered to the active version would miss its own rows.
/// </remarks>
/// <typeparam name="TEntity">The entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
public sealed class EfStateStore<TEntity, TDbContext> : IStateStore<TEntity>, IStateStoreBatchFold<TEntity>, IAsyncDisposable
    where TEntity : class, IProjectionEntity, new()
    where TDbContext : DbContext
{
    // ANSI SQLSTATE "23505" is Postgres' unique_violation. Same constant as the inline projections.
    private const string UniqueViolationSqlState = "23505";
    private const int MaxWriteAttempts = 5;

    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly ProjectionVersion _rebuildVersion;

    /// <summary>
    /// Creates a new EF state store.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    /// <param name="rebuildVersion">
    /// A live handle on the version to read and write. Its
    /// <see cref="ProjectionVersion.Current"/> is resolved per operation rather than cached,
    /// because a promotion moves it underneath a long-lived store. Omit for a projection that
    /// is never rebuilt; it then resolves to version 1 forever.
    /// </param>
    public EfStateStore(IDbContextFactory<TDbContext> contextFactory, ProjectionVersion rebuildVersion = default)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _rebuildVersion = rebuildVersion;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, TEntity>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TEntity>();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var version = _rebuildVersion.Current;
        var entities = await context.Set<TEntity>()
            .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
            .AsNoTracking()
            .Where(e => ids.Contains(e.DocumentId) && e.RebuildVersion == version)
            .ToListAsync(cancellationToken);

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
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var version = _rebuildVersion.Current;
        return await context.Set<TEntity>()
            .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
            .AsNoTracking()
            .Where(e => e.RebuildVersion == version)
            .OrderByDescending(e => e.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);

        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        // Resolved once for the whole batch, so a promotion landing mid-batch cannot split
        // attempts, upserts, and deletes across two versions.
        await WriteWithRetriesAsync(upserts, deletes, _rebuildVersion.Current, cancellationToken);
    }

    private async Task WriteWithRetriesAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        int version,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(50);

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                await WriteBatchOnceAsync(upserts, deletes, version, cancellationToken);
                return;
            }
            catch (Exception ex) when (IsRetryableConflict(ex))
            {
                AlbertoMetrics.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("kind", "projection"));

                if (attempt == MaxWriteAttempts)
                    throw ToConcurrencyConflict(ex, upserts.Keys, deletes);

                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    /// <summary>
    /// Reads the batch's rows once, tracked, and writes the fold's outcome through the same
    /// context, instead of <see cref="LoadManyAsync"/> followed by the tracked re-read in
    /// <see cref="WriteBatchOnceAsync"/>. A conflict falls back to the retry loop behind
    /// <see cref="ApplyChangesAsync"/>, which re-reads per attempt exactly as before.
    /// </summary>
    async Task<bool> IStateStoreBatchFold<TEntity>.LoadFoldApplyAsync(
        IReadOnlyList<string> documentIds,
        Func<IReadOnlyDictionary<string, TEntity>, (IReadOnlyDictionary<string, TEntity> Upserts, IReadOnlyCollection<string> Deletes)> fold,
        CancellationToken cancellationToken)
    {
        var version = _rebuildVersion.Current;

        IReadOnlyDictionary<string, TEntity> upserts = new Dictionary<string, TEntity>();
        IReadOnlyCollection<string> deletes = [];

        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            try
            {
                var outcome = await EfProjectionWriter.LoadFoldSaveAsync<TEntity>(
                    context,
                    version,
                    documentIds,
                    existing =>
                    {
                        (upserts, deletes) = fold(existing);
                        return (upserts, deletes);
                    },
                    cancellationToken);

                if (outcome.Saved)
                    return true;
                if (outcome.NothingToWrite)
                    return false;
            }
            catch (Exception ex) when (IsRetryableConflict(ex))
            {
                AlbertoMetrics.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("kind", "projection"));
            }
        }

        await WriteWithRetriesAsync(upserts, deletes, version, cancellationToken);
        return true;
    }

    /// <summary>
    /// Applies the complete change set in one fresh context. Each retry calls this method again,
    /// so no tracked state or partial fallback path can leak between attempts.
    /// </summary>
    private async Task WriteBatchOnceAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        int version,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await EfProjectionWriter.WriteAsync(context, version, upserts, deletes, cancellationToken);
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
