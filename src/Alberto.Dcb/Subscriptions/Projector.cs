namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Processor that applies a Projection to events and persists state.
/// Handles batching, storage, and checkpoints.
/// </summary>
/// <typeparam name="TState">The projection state type.</typeparam>
/// <typeparam name="TProjection">The projection definition type.</typeparam>
public abstract class Projector<TState, TProjection> : IEventProcessor
    where TProjection : Projection<TState>, new()
    where TState : new()
{
    private readonly ICheckpointStore _checkpointStore;
    private readonly TProjection _projection = new();

    protected Projector(ICheckpointStore checkpointStore)
    {
        _checkpointStore = checkpointStore;
    }

    /// <summary>
    /// Unique identifier for this processor. Used as the checkpoint key.
    /// Should be versioned (e.g., "order-summary-v1") to allow rebuilding.
    /// </summary>
    public abstract string ProcessorId { get; }

    /// <summary>
    /// Whether this processor is currently active.
    /// </summary>
    public virtual bool IsActive => true;

    /// <summary>
    /// The event types this processor handles.
    /// </summary>
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <summary>
    /// Pure projection definition. Access this for testing projection logic.
    /// </summary>
    public TProjection Projection => _projection;

    /// <summary>
    /// Process a batch of events.
    /// </summary>
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
        var states = await LoadManyAsync(byDocument.Keys, ct);

        var upserts = new Dictionary<string, TState>();
        var deletes = new List<string>();

        // Fold events per document
        foreach (var (docId, docEvents) in byDocument)
        {
            states.TryGetValue(docId, out var state);
            state ??= new TState();

            ProjectionResult<TState> result = Projection.Unchanged<TState>();

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
            await ApplyChangesAsync(upserts, deletes, ct);
        }

        // Save checkpoint
        var lastPosition = events[^1].GlobalPosition;
        await _checkpointStore.SaveAsync(ProcessorId, lastPosition, ct);

        return ProcessingResult.Continue;
    }

    /// <summary>
    /// Batch load states by document ID.
    /// </summary>
    protected abstract Task<Dictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct);

    /// <summary>
    /// Batch persist changes - upserts and deletes in one operation.
    /// </summary>
    protected abstract Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct);
}
