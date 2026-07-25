#pragma warning disable CS0618 // Type or member is obsolete — intentional, this class itself is obsolete infrastructure
namespace Alberto.Dcb.Subscriptions;

[Obsolete("Use DeclareProjection.For<TState>() instead. This type will be removed in a future version.")]
internal sealed class AsyncProjection<TState, TProjection> : IEventProcessor, IFlushable, IBatchableProcessor, IAsyncDisposable
    where TProjection : Projection<TState>, new()
    where TState : new()
{
    private readonly Func<IStateStore<TState>> _stateStoreFactory;
    private IStateStore<TState>? _stateStore;
    private readonly TProjection _projection = new();
    private volatile bool _isActive = true;
    private volatile bool _isRebuilding;
    private bool _disposed;

    private readonly Dictionary<string, TState> _pendingUpserts = new();
    private readonly HashSet<string> _pendingDeletes = new();

    public AsyncProjection(Func<IStateStore<TState>> stateStoreFactory, string processorId)
    {
        _stateStoreFactory = stateStoreFactory ?? throw new ArgumentNullException(nameof(stateStoreFactory));
        ProcessorId = processorId ?? throw new ArgumentNullException(nameof(processorId));
    }

    public string ProcessorId { get; }
    public bool IsActive { get => _isActive; set => _isActive = value; }
    public bool IsRebuilding { get => _isRebuilding; set => _isRebuilding = value; }
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    private IStateStore<TState> GetStore() => _stateStore ??= _stateStoreFactory();

    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        var docId = _projection.GetDocumentId(@event);
        var stateStore = GetStore();

        TState state;
        if (_pendingUpserts.TryGetValue(docId, out var pendingState))
            state = pendingState;
        else
        {
            var states = await stateStore.LoadManyAsync([docId], ct);
            state = states.GetValueOrDefault(docId) ?? new TState();
        }

        if (state is IProjectionEntity entity && entity.LastProcessedPosition >= @event.GlobalPosition)
            return;

        var result = _projection.Apply(state, @event);

        switch (result)
        {
            case ProjectionResult<TState>.Set s:
                if (s.State is IProjectionEntity projEntity)
                    projEntity.LastProcessedPosition = @event.GlobalPosition;
                _pendingUpserts[docId] = s.State;
                _pendingDeletes.Remove(docId);
                break;

            case ProjectionResult<TState>.Delete:
                _pendingDeletes.Add(docId);
                _pendingUpserts.Remove(docId);
                break;
        }
    }

    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
    {
        var stateStore = GetStore();
        var docIds = events.Select(e => _projection.GetDocumentId(e)).Distinct().ToList();
        var states = await stateStore.LoadManyAsync(docIds, ct);

        var upserts = new Dictionary<string, TState>();
        var deletes = new HashSet<string>();

        foreach (var evt in events)
        {
            var docId = _projection.GetDocumentId(evt);

            TState state;
            if (upserts.TryGetValue(docId, out var pendingState))
                state = pendingState;
            else if (deletes.Contains(docId))
                state = new TState();
            else
                state = states.GetValueOrDefault(docId) ?? new TState();

            if (state is IProjectionEntity entity && entity.LastProcessedPosition >= evt.GlobalPosition)
                continue;

            var result = _projection.Apply(state, evt);

            switch (result)
            {
                case ProjectionResult<TState>.Set s:
                    if (s.State is IProjectionEntity projEntity)
                        projEntity.LastProcessedPosition = evt.GlobalPosition;
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
            await stateStore.ApplyChangesAsync(upserts, deletes.ToList(), ct);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_pendingUpserts.Count == 0 && _pendingDeletes.Count == 0)
            return;

        var stateStore = GetStore();
        var upserts = new Dictionary<string, TState>(_pendingUpserts);
        var deletes = new List<string>(_pendingDeletes);
        _pendingUpserts.Clear();
        _pendingDeletes.Clear();

        await stateStore.ApplyChangesAsync(upserts, deletes, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await FlushAsync(); }
        catch { /* Best effort */ }

        if (_stateStore is IAsyncDisposable disposable)
        {
            try { await disposable.DisposeAsync(); }
            catch { /* Best effort */ }
        }
    }
}
