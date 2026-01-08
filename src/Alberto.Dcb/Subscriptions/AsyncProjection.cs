namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal processor that applies a projection asynchronously via a consumer.
/// Uses IStateStore for persistence - no inheritance required.
/// </summary>
internal sealed class AsyncProjection<TState, TProjection> : IEventProcessor
    where TProjection : Projection<TState>, new()
    where TState : new()
{
    private readonly IStateStore<TState> _stateStore;
    private readonly TProjection _projection = new();

    public AsyncProjection(IStateStore<TState> stateStore, string processorId)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        ProcessorId = processorId ?? throw new ArgumentNullException(nameof(processorId));
    }

    /// <inheritdoc/>
    public string ProcessorId { get; }

    /// <inheritdoc/>
    public bool IsActive { get; set; } = true;

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        var docId = _projection.GetDocumentId(@event);

        // Load current state
        var states = await _stateStore.LoadManyAsync([docId], transaction: null, ct);
        var state = states.GetValueOrDefault(docId) ?? new TState();

        // Apply event
        var result = _projection.Apply(state, @event);

        // Persist change
        switch (result)
        {
            case ProjectionResult<TState>.Set s:
                await _stateStore.ApplyChangesAsync(
                    new Dictionary<string, TState> { [docId] = s.State },
                    [],
                    transaction: null,
                    ct);
                break;
            case ProjectionResult<TState>.Delete:
                await _stateStore.ApplyChangesAsync(
                    new Dictionary<string, TState>(),
                    [docId],
                    transaction: null,
                    ct);
                break;
            // Unchanged: no database operation
        }
    }
}
