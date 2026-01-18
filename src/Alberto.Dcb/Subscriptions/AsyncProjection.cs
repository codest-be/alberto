using System.Collections.Concurrent;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal processor that applies a projection asynchronously via a consumer.
/// Uses IStateStore for persistence - no inheritance required.
/// Implements IFlushable to batch changes and write them all at once for better performance.
/// Implements time-based eviction for inactive tenant state stores to prevent memory leaks.
/// </summary>
internal sealed class AsyncProjection<TState, TProjection> : IEventProcessor, IFlushable, IAsyncDisposable
    where TProjection : Projection<TState>, new()
    where TState : new()
{
    private readonly Func<string, IStateStore<TState>> _stateStoreFactory;
    private readonly ConcurrentDictionary<string, (IStateStore<TState> Store, DateTimeOffset LastAccess)> _stateStoreCache = new();
    private readonly TProjection _projection = new();
    private readonly Timer _evictionTimer;
    private readonly TimeSpan _maxIdleTime = TimeSpan.FromMinutes(30);
    private volatile bool _isActive = true;
    private volatile bool _isRebuilding;
    private bool _disposed;

    // Batching: accumulate changes per tenant, then flush all at once
    private readonly ConcurrentDictionary<string, Dictionary<string, TState>> _pendingUpserts = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _pendingDeletes = new();

    public AsyncProjection(Func<string, IStateStore<TState>> stateStoreFactory, string processorId)
    {
        _stateStoreFactory = stateStoreFactory ?? throw new ArgumentNullException(nameof(stateStoreFactory));
        ProcessorId = processorId ?? throw new ArgumentNullException(nameof(processorId));
        _evictionTimer = new Timer(OnEvictionTimer, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
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
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        var docId = _projection.GetDocumentId(@event);
        var tenantId = @event.TenantId;

        // Get or create state store, update last access time
        var entry = _stateStoreCache.AddOrUpdate(
            tenantId,
            _ => (_stateStoreFactory(tenantId), DateTimeOffset.UtcNow),
            (_, existing) => (existing.Store, DateTimeOffset.UtcNow));

        var stateStore = entry.Store;

        // Check pending upserts first (for events building on previous events in same batch)
        // then load fresh from store (fixes EF parallel projection issues with detached entities)
        TState state;
        if (_pendingUpserts.TryGetValue(tenantId, out var pendingUpserts) &&
            pendingUpserts.TryGetValue(docId, out var pendingState))
        {
            state = pendingState;
        }
        else
        {
            var states = await stateStore.LoadManyAsync([docId], transaction: null, ct);
            state = states.GetValueOrDefault(docId) ?? new TState();
        }

        // Idempotency check: skip if this event was already processed
        if (state is IProjectionEntity entity && entity.LastProcessedPosition >= @event.GlobalPosition)
        {
            return; // Event already processed, skip
        }

        // Apply event
        var result = _projection.Apply(state, @event);

        // Accumulate changes for batch flush
        switch (result)
        {
            case ProjectionResult<TState>.Set s:
                // Update LastProcessedPosition for idempotency
                if (s.State is IProjectionEntity projEntity)
                {
                    projEntity.LastProcessedPosition = @event.GlobalPosition;
                }

                // Add to pending upserts
                var upserts = _pendingUpserts.GetOrAdd(tenantId, _ => new Dictionary<string, TState>());
                upserts[docId] = s.State;

                // Remove from pending deletes if it was there
                if (_pendingDeletes.TryGetValue(tenantId, out var deletes))
                {
                    deletes.Remove(docId);
                }
                break;

            case ProjectionResult<TState>.Delete:
                // Add to pending deletes
                var deleteSet = _pendingDeletes.GetOrAdd(tenantId, _ => new HashSet<string>());
                deleteSet.Add(docId);

                // Remove from pending upserts if it was there
                if (_pendingUpserts.TryGetValue(tenantId, out var upsertsToRemoveFrom))
                {
                    upsertsToRemoveFrom.Remove(docId);
                }
                break;
                // Unchanged: no operation needed
        }
    }

    /// <summary>
    /// Flushes all pending changes to the underlying state stores.
    /// Called by the consumer after processing a batch of events.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Process each tenant's pending changes
        foreach (var tenantId in _pendingUpserts.Keys.Union(_pendingDeletes.Keys).ToList())
        {
            // Get the state store for this tenant
            if (!_stateStoreCache.TryGetValue(tenantId, out var entry))
            {
                continue; // No state store for this tenant (shouldn't happen)
            }

            var stateStore = entry.Store;

            // Get pending changes for this tenant
            _pendingUpserts.TryRemove(tenantId, out var upserts);
            _pendingDeletes.TryRemove(tenantId, out var deletes);

            var upsertsToApply = upserts ?? new Dictionary<string, TState>();
            var deletesToApply = deletes ?? new HashSet<string>();

            if (upsertsToApply.Count == 0 && deletesToApply.Count == 0)
            {
                continue; // No changes for this tenant
            }

            // Apply all changes in a single call
            await stateStore.ApplyChangesAsync(
                upsertsToApply,
                deletesToApply.ToList(),
                transaction: null,
                ct);
        }
    }

    private async void OnEvictionTimer(object? state)
    {
        if (_disposed) return;

        var cutoff = DateTimeOffset.UtcNow - _maxIdleTime;
        var toEvict = _stateStoreCache
            .Where(kvp => kvp.Value.LastAccess < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var tenantId in toEvict)
        {
            if (_stateStoreCache.TryRemove(tenantId, out var entry))
            {
                if (entry.Store is IAsyncDisposable disposable)
                {
                    try { await disposable.DisposeAsync(); }
                    catch { /* Best effort */ }
                }
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _evictionTimer.DisposeAsync();

        // Flush any pending changes before disposing
        try
        {
            await FlushAsync();
        }
        catch
        {
            // Best effort flush on dispose
        }

        foreach (var entry in _stateStoreCache.Values)
        {
            if (entry.Store is IAsyncDisposable disposable)
            {
                try { await disposable.DisposeAsync(); }
                catch { /* Best effort */ }
            }
        }
        _stateStoreCache.Clear();
        _pendingUpserts.Clear();
        _pendingDeletes.Clear();
    }
}
