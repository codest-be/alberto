using System.Collections.Concurrent;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal processor that executes a <see cref="ProjectionDeclaration{TState}"/> asynchronously.
/// No reflection — all dispatch is delegate-based.
/// Mirrors the batching, eviction, and idempotency behaviour of <see cref="AsyncProjection{TState,TProjection}"/>.
/// </summary>
internal sealed class DeclaredAsyncProjection<TState> : IBatchableProcessor, IFlushable, IAsyncDisposable
    where TState : new()
{
    private readonly ProjectionDeclaration<TState> _declaration;
    private readonly Func<string, IStateStore<TState>> _stateStoreFactory;
    private readonly ConcurrentDictionary<string, (IStateStore<TState> Store, DateTimeOffset LastAccess)> _stateStoreCache = new();
    private readonly Timer _evictionTimer;
    private readonly TimeSpan _maxIdleTime = TimeSpan.FromMinutes(30);
    private volatile bool _isActive = true;
    private volatile bool _isRebuilding;
    private bool _disposed;

    public DeclaredAsyncProjection(
        ProjectionDeclaration<TState> declaration,
        Func<string, IStateStore<TState>> stateStoreFactory,
        string? processorIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);
        _declaration = declaration;
        _stateStoreFactory = stateStoreFactory;
        ProcessorId = processorIdOverride ?? declaration.ProcessorId;
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
    public IReadOnlySet<string> HandledEventTypes => _declaration.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        if (!_declaration.HandledEventTypes.Contains(@event.EventType.Id)) return;

        var docId = _declaration.GetDocumentIdFunc(@event);
        if (docId is null) return;

        var tenantId = @event.TenantId ?? string.Empty;
        var stateStore = GetOrCreateStore(tenantId);

        var states = await stateStore.LoadManyAsync([docId], transaction: null, ct);
        var state = states.GetValueOrDefault(docId) ?? _declaration.InitialState();

        // Idempotency check
        if (state is IProjectionEntity entity && entity.LastProcessedPosition >= @event.GlobalPosition)
            return;

        var ctx = ProjectionContext.FromEnvelope(@event);
        var result = _declaration.EvolveFunc(state, @event, ctx);

        switch (result)
        {
            case ProjectionResult<TState>.Set s:
                if (s.State is IProjectionEntity pe) pe.LastProcessedPosition = @event.GlobalPosition;
                await stateStore.ApplyChangesAsync(
                    new Dictionary<string, TState> { [docId] = s.State },
                    [],
                    transaction: null,
                    ct);
                break;

            case ProjectionResult<TState>.Delete:
                await stateStore.ApplyChangesAsync(
                    new Dictionary<string, TState>(),
                    [docId],
                    transaction: null,
                    ct);
                break;

            // Unchanged: no operation needed
        }
    }

    /// <inheritdoc/>
    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
    {
        // Filter to events this projection handles
        var relevant = events.Where(e => _declaration.HandledEventTypes.Contains(e.EventType.Id)).ToList();
        if (relevant.Count == 0) return;

        // Group by tenant so we can do one LoadMany + one ApplyChanges per tenant
        var byTenant = relevant.GroupBy(e => e.TenantId ?? string.Empty);

        foreach (var tenantGroup in byTenant)
        {
            var tenantId = tenantGroup.Key;
            var stateStore = GetOrCreateStore(tenantId);
            var tenantEvents = tenantGroup.ToList();

            // 1. Build the docId map — skip events where GetDocumentIdFunc returns null
            var docIdMap = new Dictionary<IEventEnvelope, string>(ReferenceEqualityComparer.Instance);
            foreach (var evt in tenantEvents)
            {
                var docId = _declaration.GetDocumentIdFunc(evt);
                if (docId is not null)
                    docIdMap[evt] = docId;
            }

            if (docIdMap.Count == 0) continue;

            // 2. ONE LoadMany for the whole tenant batch
            var states = await stateStore.LoadManyAsync(docIdMap.Values.Distinct(), transaction: null, ct);

            // 3. Apply events in order, accumulating changes in memory
            var upserts = new Dictionary<string, TState>();
            var deletes = new HashSet<string>();

            foreach (var evt in tenantEvents)
            {
                if (!docIdMap.TryGetValue(evt, out var docId)) continue;

                // State resolution: pending upsert → pending delete reset → loaded → initial
                TState state;
                if (upserts.TryGetValue(docId, out var pendingState))
                    state = pendingState;
                else if (deletes.Contains(docId))
                    state = _declaration.InitialState();
                else
                    state = states.GetValueOrDefault(docId) ?? _declaration.InitialState();

                // Idempotency check
                if (state is IProjectionEntity entity && entity.LastProcessedPosition >= evt.GlobalPosition)
                    continue;

                var ctx = ProjectionContext.FromEnvelope(evt);
                var result = _declaration.EvolveFunc(state, evt, ctx);

                switch (result)
                {
                    case ProjectionResult<TState>.Set s:
                        if (s.State is IProjectionEntity pe) pe.LastProcessedPosition = evt.GlobalPosition;
                        upserts[docId] = s.State;
                        deletes.Remove(docId);
                        break;

                    case ProjectionResult<TState>.Delete:
                        deletes.Add(docId);
                        upserts.Remove(docId);
                        break;

                    // Unchanged: no operation needed
                }
            }

            // 4. ONE ApplyChanges for the whole tenant batch
            if (upserts.Count > 0 || deletes.Count > 0)
                await stateStore.ApplyChangesAsync(upserts, deletes.ToList(), transaction: null, ct);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="ProcessBatchAsync"/> applies changes inline, so there is nothing to flush.
    /// This method exists to satisfy the <see cref="IFlushable"/> contract when the
    /// single-event path (<see cref="ProcessEventAsync"/>) is used instead.
    /// </remarks>
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    private IStateStore<TState> GetOrCreateStore(string tenantId)
    {
        var entry = _stateStoreCache.AddOrUpdate(
            tenantId,
            _ => (_stateStoreFactory(tenantId), DateTimeOffset.UtcNow),
            (_, existing) => (existing.Store, DateTimeOffset.UtcNow));
        return entry.Store;
    }

    private async void OnEvictionTimer(object? _)
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
        _isActive = false;

        await _evictionTimer.DisposeAsync();

        foreach (var entry in _stateStoreCache.Values)
        {
            if (entry.Store is IAsyncDisposable disposable)
            {
                try { await disposable.DisposeAsync(); }
                catch { /* Best effort */ }
            }
        }

        _stateStoreCache.Clear();
    }
}
