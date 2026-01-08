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
    private readonly ICheckpointStore _checkpointStore;
    private readonly TProjection _projection = new();

    public AsyncProjection(
        IStateStore<TState> stateStore,
        ICheckpointStore checkpointStore,
        string processorId)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        ProcessorId = processorId ?? throw new ArgumentNullException(nameof(processorId));
    }

    /// <inheritdoc/>
    public string ProcessorId { get; }

    /// <inheritdoc/>
    public bool IsActive => true;

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <inheritdoc/>
    public async Task<ProcessingResult> ProcessBatchAsync(
        IReadOnlyList<IEventEnvelope> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0)
            return ProcessingResult.Continue;

        // Group events by document ID
        var byDocument = events
            .GroupBy(e => _projection.GetDocumentId(e))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Load all states in one batch
        var states = await _stateStore.LoadManyAsync(byDocument.Keys, transaction: null, ct);

        var upserts = new Dictionary<string, TState>();
        var deletes = new List<string>();

        // Fold events per document
        foreach (var (docId, docEvents) in byDocument)
        {
            states.TryGetValue(docId, out var state);
            state ??= new TState();

            ProjectionResult<TState> result = ProjectionResults.Unchanged<TState>();

            foreach (var envelope in docEvents)
            {
                // Update state from previous result
                state = result switch
                {
                    ProjectionResult<TState>.Set s => s.State,
                    _ => state
                };
                result = _projection.Apply(state, envelope);
            }

            // Classify final result
            switch (result)
            {
                case ProjectionResult<TState>.Set s:
                    upserts[docId] = s.State;
                    break;
                case ProjectionResult<TState>.Delete:
                    deletes.Add(docId);
                    break;
                // Unchanged: no database operation
            }
        }

        // Persist all changes
        if (upserts.Count > 0 || deletes.Count > 0)
        {
            await _stateStore.ApplyChangesAsync(upserts, deletes, transaction: null, ct);
        }

        // Save checkpoint
        var lastPosition = events[^1].GlobalPosition;
        await _checkpointStore.SaveAsync(ProcessorId, lastPosition, ct);

        return ProcessingResult.Continue;
    }
}
