using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Dcb.EntityFramework.Batching;

/// <summary>
/// Event processor that accumulates changes across a batch and calls SaveChanges once.
/// Ideal for complex projections that touch multiple tables per event.
/// </summary>
/// <typeparam name="TDbContext">The DbContext type to use.</typeparam>
/// <typeparam name="THandler">The batch handler implementation type.</typeparam>
public sealed class BatchedEfProjection<TDbContext, THandler> : IEventProcessor
    where TDbContext : DbContext
    where THandler : IEfBatchHandler<TDbContext>, new()
{
    private readonly IDbContextFactory<TDbContext> _contextFactory;
    private readonly THandler _handler = new();
    private TDbContext? _currentContext;
    private int _pendingChanges;

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
    public bool IsActive { get; set; } = true;

    /// <inheritdoc/>
    public bool IsRebuilding { get; set; }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _handler.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        // Create context if not already created (start of batch)
        _currentContext ??= await _contextFactory.CreateDbContextAsync(ct);

        // Apply the event
        await _handler.ApplyAsync(_currentContext, @event, ct);
        _pendingChanges++;
    }

    /// <summary>
    /// Flushes any pending changes to the database.
    /// Should be called at the end of each batch by the consumer.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_currentContext != null && _pendingChanges > 0)
        {
            await _currentContext.SaveChangesAsync(ct);
            await _currentContext.DisposeAsync();
            _currentContext = null;
            _pendingChanges = 0;
        }
    }
}
