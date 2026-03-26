using System.Data;

#pragma warning disable CS0618 // Obsolete projection types used intentionally for backward-compatibility
namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Wraps a Projection&lt;TState&gt; for inline execution within an event commit transaction.
/// Uses the same pure projection definition as async projections, but runs in the same
/// transaction as the event append for strong consistency.
/// </summary>
/// <typeparam name="TState">The projection state type.</typeparam>
/// <typeparam name="TProjection">The projection implementation type.</typeparam>
internal class InlineProjection<TState, TProjection> : IInlineProjection
    where TProjection : Projection<TState>, new()
    where TState : new()
{
    private readonly TProjection _projection = new();
    private readonly IStateStore<TState> _stateStore;

    /// <summary>
    /// Creates a new inline projection.
    /// </summary>
    /// <param name="stateStore">The state store for persisting projection state.</param>
    public InlineProjection(IStateStore<TState> stateStore)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    /// <summary>
    /// The underlying projection for testing purposes.
    /// </summary>
    public TProjection Projection => _projection;

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessAsync(
        IReadOnlyList<IEventEnvelope> events,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        // Filter to events we handle and group by document ID
        var byDocument = events
            .Where(e => HandledEventTypes.Contains(e.EventType.Id))
            .GroupBy(e => _projection.GetDocumentId(e))
            .ToDictionary(g => g.Key, g => g.ToList());

        if (byDocument.Count == 0)
            return;

        // Load current state for all affected documents
        var states = await _stateStore.LoadManyAsync(byDocument.Keys, transaction, ct);

        var upserts = new Dictionary<string, TState>();
        var deletes = new List<string>();

        // Process events for each document
        foreach (var (docId, docEvents) in byDocument)
        {
            states.TryGetValue(docId, out var state);
            state ??= new TState();

            // Fold all events for this document
            ProjectionResult<TState> result = ProjectionResults.Unchanged<TState>();
            foreach (var envelope in docEvents)
            {
                // Get state from previous result if it was a Set
                state = result switch
                {
                    ProjectionResult<TState>.Set s => s.State,
                    _ => state
                };
                result = _projection.Apply(state, envelope);
            }

            // Collect the final result
            switch (result)
            {
                case ProjectionResult<TState>.Set s:
                    upserts[docId] = s.State;
                    break;
                case ProjectionResult<TState>.Delete:
                    deletes.Add(docId);
                    break;
                // Unchanged: no action needed
            }
        }

        // Apply all changes within the transaction
        if (upserts.Count > 0 || deletes.Count > 0)
        {
            await _stateStore.ApplyChangesAsync(upserts, deletes, transaction, ct);
        }
    }
}
