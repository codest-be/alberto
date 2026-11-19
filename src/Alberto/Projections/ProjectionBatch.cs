namespace Alberto.Projections;

/// <summary>
/// The rules for folding events onto projection documents, with no I/O, so every host applies the
/// same ones: the async processor, the inline EF projection and the <c>ProjectionSpec</c> DSL
/// differ only in how they load documents and persist the outcome.
/// </summary>
/// <remarks>
/// Split in two so a host can load between the halves: <see cref="ProjectionBatch{TState}"/>
/// answers "which documents does this batch touch", <see cref="ProjectionChanges{TState}"/> folds
/// the events onto them.
/// </remarks>
internal sealed class ProjectionBatch<TState> where TState : new()
{
    private readonly ProjectionDeclaration<TState> _declaration;
    private readonly List<(IEventEnvelope Event, IProjectionEventHandler<TState> Handler, object Parsed, string DocumentId)> _steps = [];

    /// <summary>
    /// Skips event types the declaration does not handle, parses each payload exactly once
    /// (reused for both the document id and Apply, avoiding a second deserialization) and drops
    /// events that route to no document.
    /// </summary>
    internal ProjectionBatch(
        ProjectionDeclaration<TState> declaration,
        IReadOnlyList<IEventEnvelope> events,
        EventSerializer? serializer)
    {
        _declaration = declaration;
        Handled = new List<IEventEnvelope>(events.Count);

        foreach (var evt in events)
        {
            if (!declaration.HandledEventTypes.Contains(evt.EventType.Id)) continue;
            Handled.Add(evt);

            var handler = declaration.Handlers[evt.EventType.Id];
            var parsed = handler.ParseEvent(evt, serializer);
            var documentId = handler.GetDocumentId(parsed);
            if (documentId is null) continue;

            _steps.Add((evt, handler, parsed, documentId));
        }

        DocumentIds = _steps.Select(s => s.DocumentId).Distinct().ToList();
    }

    /// <summary>
    /// Every event of a handled type, including those that route to no document: a host's
    /// after-commit hook sees all of them, not only the ones that changed something.
    /// </summary>
    internal List<IEventEnvelope> Handled { get; }

    /// <summary>The documents the batch touches, each once. Empty when nothing routes anywhere.</summary>
    internal IReadOnlyList<string> DocumentIds { get; }

    /// <summary>Folds the batch, in order, onto the documents the host loaded.</summary>
    internal ProjectionChanges<TState> Fold(IReadOnlyDictionary<string, TState> loaded)
    {
        var changes = new ProjectionChanges<TState>(_declaration, loaded);
        foreach (var (evt, handler, parsed, documentId) in _steps)
            changes.Fold(handler, documentId, parsed, ProjectionContext.FromEnvelope(evt));
        return changes;
    }
}

/// <summary>
/// The documents a run of events has written and deleted so far, and the per-event step that
/// grows them.
/// </summary>
internal sealed class ProjectionChanges<TState> where TState : new()
{
    private readonly ProjectionDeclaration<TState> _declaration;

    private readonly IReadOnlyDictionary<string, TState> _loaded;

    /// <param name="declaration">The projection being folded.</param>
    /// <param name="loaded">Documents the host loaded up front; omitted when every document starts fresh.</param>
    internal ProjectionChanges(ProjectionDeclaration<TState> declaration, IReadOnlyDictionary<string, TState>? loaded = null)
    {
        _declaration = declaration;
        _loaded = loaded ?? new Dictionary<string, TState>();
    }

    internal Dictionary<string, TState> Upserts { get; } = new();

    internal HashSet<string> Deletes { get; } = new();

    /// <summary>
    /// Folds one parsed event onto its document. The current state is the pending upsert, else a
    /// fresh one if the document was deleted earlier in this run, else the loaded one, else a
    /// fresh one. Returns <see langword="null"/> when the document has already seen this
    /// position (the idempotency guard), otherwise the handler's result.
    /// </summary>
    internal ProjectionResult<TState>? Fold(
        IProjectionEventHandler<TState> handler,
        string documentId,
        object parsed,
        ProjectionContext context)
    {
        TState state;
        if (Upserts.TryGetValue(documentId, out var pending))
            state = pending;
        else if (Deletes.Contains(documentId))
            state = _declaration.InitialState();
        else
            state = _loaded.GetValueOrDefault(documentId) ?? _declaration.InitialState();

        if (state is IProjectionEntity entity && entity.LastProcessedPosition >= context.Position)
            return null;

        var result = handler.Apply(state, parsed, context);

        switch (result)
        {
            case ProjectionResult<TState>.Set set:
                if (set.State is IProjectionEntity written)
                    ProjectionEntityStamp.LastProcessedPosition(written, context.Position);
                Upserts[documentId] = set.State;
                Deletes.Remove(documentId);
                break;

            case ProjectionResult<TState>.Delete:
                Deletes.Add(documentId);
                Upserts.Remove(documentId);
                break;
        }

        return result;
    }
}
