namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal processor that executes a <see cref="ProjectionDeclaration{TState}"/> asynchronously.
/// No reflection — all dispatch is delegate-based.
/// </summary>
internal sealed class DeclaredAsyncProjection<TState> : IBatchableProcessor, IFlushable, IAsyncDisposable
    where TState : new()
{
    private readonly ProjectionDeclaration<TState> _declaration;
    private readonly Func<IStateStore<TState>> _stateStoreFactory;
    private IStateStore<TState>? _stateStore;
    private volatile bool _isActive = true;
    private volatile bool _isRebuilding;
    private bool _disposed;

    public DeclaredAsyncProjection(
        ProjectionDeclaration<TState> declaration,
        Func<IStateStore<TState>> stateStoreFactory,
        string? processorIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);
        _declaration = declaration;
        _stateStoreFactory = stateStoreFactory;
        ProcessorId = processorIdOverride ?? declaration.ProcessorId;
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

    private IStateStore<TState> GetStore() => _stateStore ??= _stateStoreFactory();

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        if (!_declaration.HandledEventTypes.Contains(@event.EventType.Id)) return;

        var docId = _declaration.GetDocumentId(@event);
        if (docId is null) return;

        var stateStore = GetStore();
        var states = await stateStore.LoadManyAsync([docId], transaction: null, ct);
        var state = states.GetValueOrDefault(docId) ?? _declaration.InitialState();

        if (state is IProjectionEntity entity && entity.LastProcessedPosition >= @event.GlobalPosition)
            return;

        var ctx = ProjectionContext.FromEnvelope(@event);
        var result = _declaration.Apply(state, @event, ctx);

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
        }
    }

    /// <inheritdoc/>
    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
    {
        var relevant = events.Where(e => _declaration.HandledEventTypes.Contains(e.EventType.Id)).ToList();
        if (relevant.Count == 0) return;

        var stateStore = GetStore();

        var docIdMap = new Dictionary<IEventEnvelope, string>(ReferenceEqualityComparer.Instance);
        foreach (var evt in relevant)
        {
            var docId = _declaration.GetDocumentId(evt);
            if (docId is not null)
                docIdMap[evt] = docId;
        }

        if (docIdMap.Count == 0) return;

        var states = await stateStore.LoadManyAsync(docIdMap.Values.Distinct(), transaction: null, ct);

        var upserts = new Dictionary<string, TState>();
        var deletes = new HashSet<string>();

        foreach (var evt in relevant)
        {
            if (!docIdMap.TryGetValue(evt, out var docId)) continue;

            TState state;
            if (upserts.TryGetValue(docId, out var pendingState))
                state = pendingState;
            else if (deletes.Contains(docId))
                state = _declaration.InitialState();
            else
                state = states.GetValueOrDefault(docId) ?? _declaration.InitialState();

            if (state is IProjectionEntity entity && entity.LastProcessedPosition >= evt.GlobalPosition)
                continue;

            var ctx = ProjectionContext.FromEnvelope(evt);
            var result = _declaration.Apply(state, evt, ctx);

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
            }
        }

        if (upserts.Count > 0 || deletes.Count > 0)
            await stateStore.ApplyChangesAsync(upserts, deletes.ToList(), transaction: null, ct);
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _isActive = false;

        if (_stateStore is IAsyncDisposable disposable)
        {
            try { await disposable.DisposeAsync(); }
            catch { /* Best effort */ }
        }
    }
}
