using System.Data.Common;
using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

#pragma warning disable CS0618 // Obsolete projection types used intentionally for backward-compatibility
namespace Alberto.Dcb.EntityFramework.Inline;

/// <summary>
/// Inline projection that uses Entity Framework for storage.
/// Runs synchronously after event persistence for read-your-writes consistency.
/// </summary>
/// <typeparam name="TEntity">The EF entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TProjection">The projection type.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
internal sealed class EfInlineProjection<TEntity, TProjection, TDbContext> : IInlineProjection
    where TEntity : class, IProjectionEntity, new()
    where TProjection : Projection<TEntity>, new()
    where TDbContext : DbContext
{
    // See DeclaredEfInlineProjection for rationale — same retry shape applies here.
    private const int MaxConcurrencyRetries = 5;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(10);

    // ANSI SQLSTATE "23505" is Postgres' unique_violation. Provider-agnostic via DbException.SqlState.
    private const string UniqueViolationSqlState = "23505";

    private readonly TProjection _projection = new();
    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly ILogger<EfInlineProjection<TEntity, TProjection, TDbContext>>? _logger;

    /// <summary>
    /// Creates a new inline EF projection.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    /// <param name="logger">Optional logger for retry-exhaustion alerts.</param>
    public EfInlineProjection(
        IDbContextFactory<TDbContext> contextFactory,
        ILogger<EfInlineProjection<TEntity, TProjection, TDbContext>>? logger = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        CancellationToken ct = default)
    {
        // Filter to events we handle and group by document ID using an explicit loop to avoid
        // the intermediate IGrouping allocations that LINQ GroupBy would produce (PERF-17).
        var byDocument = new Dictionary<string, List<IEventEnvelope>>();
        foreach (var e in events)
        {
            if (!HandledEventTypes.Contains(e.EventType.Id))
                continue;

            var docId = _projection.GetDocumentId(e) ?? string.Empty;
            if (!byDocument.TryGetValue(docId, out var list))
            {
                list = [];
                byDocument[docId] = list;
            }

            list.Add(e);
        }

        if (byDocument.Count == 0)
            return;

        // Hoist the key list once — it doesn't change between retries.
        var documentKeys = byDocument.Keys.ToList();

        var attempt = 0;
        while (true)
        {
            try
            {
                await ProcessOnceAsync(byDocument, documentKeys, ct);
                return;
            }
            catch (Exception ex) when (IsRetryableConflict(ex))
            {
                attempt++;
                if (attempt >= MaxConcurrencyRetries)
                {
                    // All retries exhausted. The events are durable in the store but the
                    // inline projection has not reflected them. Log a critical alert so
                    // operators can trigger a manual replay and prevent silent divergence.
                    _logger?.LogCritical(
                        ex,
                        "Inline EF projection '{ProjectionType}' failed after {Attempts} concurrency retries " +
                        "for {DocumentCount} document(s). The projection is now diverged from the event " +
                        "store for the affected documents. Manual replay or async catch-up is required.",
                        typeof(TProjection).Name,
                        attempt,
                        documentKeys.Count);

                    throw new InlineProjectionExhaustedException(
                        typeof(TProjection).Name,
                        attempts: attempt,
                        documentCount: documentKeys.Count,
                        innerException: ex);
                }

                var delayMs = (int)(InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                var jitter = Random.Shared.Next(0, delayMs);
                await Task.Delay(delayMs + jitter, ct);
            }
        }
    }

    private async Task ProcessOnceAsync(
        Dictionary<string, List<IEventEnvelope>> byDocument,
        List<string> documentKeys,
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Load current state for all affected documents.
        // Untracked: Apply may produce a fresh entity instance that we then Update; if the
        // original were tracked, Update would throw an identity-map conflict on the same key.
        var existingEntities = await context.Set<TEntity>()
            .AsNoTracking()
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

    private static bool IsRetryableConflict(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException { InnerException: DbException { SqlState: UniqueViolationSqlState } } => true,
        _ => false,
    };
}
