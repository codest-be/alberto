using System.Data;
using System.Data.Common;
using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    // Concurrency-token retries. When two inline projections fan into the same row from
    // different appends (e.g. multiple event types projected onto a shared aggregate row
    // like Source), a stale `xmin` can cause "0 rows affected" (DbUpdateConcurrencyException)
    // or — if the concurrent writer just inserted the row between our pre-load and SaveChanges
    // — a unique-constraint violation on the primary key (DbUpdateException, PG SqlState 23505).
    // The fold is a pure function of `existing + events`, so we can safely re-read and re-apply
    // under both shapes.
    private const int MaxConcurrencyRetries = 5;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(10);

    // ANSI SQLSTATE "23505" is Postgres' unique_violation. Provider-agnostic via DbException.SqlState.
    private const string UniqueViolationSqlState = "23505";

    private readonly ProjectionDeclaration<TEntity> _declaration;
    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly ILogger<DeclaredEfInlineProjection<TEntity, TDbContext>>? _logger;

    public DeclaredEfInlineProjection(
        ProjectionDeclaration<TEntity> declaration,
        IDbContextFactory<TDbContext> contextFactory,
        ILogger<DeclaredEfInlineProjection<TEntity, TDbContext>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(contextFactory);
        _declaration = declaration;
        _contextFactory = contextFactory;
        _logger = logger;
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

        var documentKeys = docIdMap.Values.Distinct().ToList();

        // Retry only makes sense when we own the transaction. With an external transaction,
        // a concurrency exception aborts the whole transaction and reissuing SaveChanges
        // would fail; the caller has to retry the entire append.
        var ownsTransaction = transaction is null;

        var attempt = 0;
        while (true)
        {
            try
            {
                await ProcessOnceAsync(events, docIdMap, documentKeys, transaction, ct);
                return;
            }
            catch (Exception ex) when (ownsTransaction && IsRetryableConflict(ex))
            {
                attempt++;
                if (attempt >= MaxConcurrencyRetries)
                {
                    // All retries exhausted. The events are durable in the store but the
                    // inline projection has not reflected them. Log a critical alert so
                    // operators can trigger a manual replay and prevent silent divergence.
                    _logger?.LogCritical(
                        ex,
                        "Inline EF projection '{ProcessorId}' failed after {Attempts} concurrency retries " +
                        "for {DocumentCount} document(s). The projection is now diverged from the event " +
                        "store for the affected documents. Manual replay or async catch-up is required.",
                        _declaration.ProcessorId,
                        attempt,
                        documentKeys.Count);

                    throw new InlineProjectionExhaustedException(
                        _declaration.ProcessorId,
                        attempts: attempt,
                        documentCount: documentKeys.Count,
                        innerException: ex);
                }

                // Exponential backoff with mild jitter to scatter concurrent retriers.
                var delayMs = (int)(InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                var jitter = Random.Shared.Next(0, delayMs);
                await Task.Delay(delayMs + jitter, ct);
            }
        }
    }

    private async Task ProcessOnceAsync(
        IReadOnlyList<IEventEnvelope> events,
        Dictionary<IEventEnvelope, string> docIdMap,
        List<string> documentKeys,
        IDbTransaction? transaction,
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Join the append transaction if one was provided. Today the event store always
        // passes null — inline runs in its own transaction directly after the append commits.
        if (transaction is System.Data.Common.DbTransaction dbTransaction)
        {
            await context.Database.UseTransactionAsync(dbTransaction, ct);
        }

        // Load existing rows untracked: the projection rebuilds the entity by `with`-ing the
        // loaded state, producing a new instance that we then Update/Add. If the original
        // instance were tracked, Update would throw an identity-map conflict on the same key.
        var existing = await context.Set<TEntity>()
            .AsNoTracking()
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

    private static bool IsRetryableConflict(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException { InnerException: DbException { SqlState: UniqueViolationSqlState } } => true,
        _ => false,
    };
}

/// <summary>
/// Thrown when an inline EF projection exhausts all concurrency retries without successfully
/// committing. The events are durable in the event store, but the inline projection view is
/// diverged for the affected documents. Operators should trigger an async replay to recover.
/// </summary>
public sealed class InlineProjectionExhaustedException : Exception
{
    /// <summary>The processor ID of the projection that exhausted its retries.</summary>
    public string ProcessorId { get; }

    /// <summary>The number of attempts made before giving up.</summary>
    public int Attempts { get; }

    /// <summary>The number of documents whose projection state is now diverged.</summary>
    public int DocumentCount { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="InlineProjectionExhaustedException"/>.
    /// </summary>
    public InlineProjectionExhaustedException(
        string processorId,
        int attempts,
        int documentCount,
        Exception? innerException = null)
        : base(
            $"Inline projection '{processorId}' exhausted {attempts} concurrency retries for " +
            $"{documentCount} document(s). The projection view is diverged; async replay is required.",
            innerException)
    {
        ProcessorId = processorId;
        Attempts = attempts;
        DocumentCount = documentCount;
    }
}
