using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Dcb.EntityFramework.Batching;

/// <summary>
/// Event processor that accumulates changes across a batch and calls SaveChanges once.
/// Ideal for complex projections that touch multiple tables per event.
/// </summary>
/// <typeparam name="TDbContext">The DbContext type to use.</typeparam>
/// <typeparam name="THandler">The batch handler implementation type.</typeparam>
public sealed class BatchedEfProjection<TDbContext, THandler> : IBatchableProcessor, IProcessorLifecycle
    where TDbContext : DbContext
    where THandler : IEfBatchHandler<TDbContext>, new()
{
    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly THandler _handler = new();
    private volatile bool _isActive = true;
    private volatile bool _isRebuilding;

    /// <summary>
    /// Creates a new batched EF projection processor.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    /// <param name="processorId">Optional processor ID.</param>
    public BatchedEfProjection(
        IDbContextFactory<TDbContext> contextFactory,
        string? processorId = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        ProcessorId = processorId ?? typeof(THandler).Name;
    }

    /// <inheritdoc/>
    public string ProcessorId { get; }

    /// <inheritdoc/>
    public bool IsActive
    {
        get => _isActive;
        set => _isActive = value;
    }

    /// <inheritdoc/>
    public bool IsRebuilding
    {
        get => _isRebuilding;
        set => _isRebuilding = value;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _handler.HandledEventTypes;

    /// <inheritdoc/>
    public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default) =>
        ProcessBatchAsync([@event], ct);

    /// <inheritdoc/>
    public async Task ProcessBatchAsync(
        IReadOnlyList<IEventEnvelope> events,
        CancellationToken ct = default)
    {
        if (!_isActive || events.Count == 0)
            return;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        foreach (var @event in events)
            await _handler.ApplyAsync(context, @event, ct);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            AlbertoMetrics.ConcurrencyConflicts.Add(1);

            var conflictedEntry = ex.Entries.FirstOrDefault();
            var conflictedDocId =
                (conflictedEntry?.Entity as IProjectionEntity)?.DocumentId ?? "unknown";

            throw new ConcurrencyConflictException(conflictedDocId, ex);
        }
    }
}
