using Alberto.Subscriptions;

namespace Alberto.Testing;

/// <summary>
/// What a projection specification actually is underneath: the documents, the context the next
/// event will carry, and the fold that turns one into the other. The stages of the chain are
/// facades over one of these — they decide which verbs are reachable, this decides what they do.
/// </summary>
internal sealed class ProjectionFold<TState> where TState : new()
{
    private static readonly IReadOnlyDictionary<string, string> NoMetadata =
        new Dictionary<string, string>();

    private readonly ProjectionDeclaration<TState> _declaration;
    private readonly Dictionary<string, TState> _documents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    private Dictionary<string, TState> _before = new(StringComparer.Ordinal);
    private string? _tenantId;
    private DateTimeOffset _timestamp = ProjectionSpec.Epoch;
    private IReadOnlyDictionary<string, string> _metadata = NoMetadata;
    private long _position;

    internal ProjectionFold(ProjectionDeclaration<TState> declaration) => _declaration = declaration;

    /// <summary>The documents as they stand.</summary>
    internal IReadOnlyDictionary<string, TState> Documents => _documents;

    /// <summary>The documents as they stood before the most recent <c>When</c>.</summary>
    internal IReadOnlyDictionary<string, TState> Before => _before;

    /// <summary>The document ids the most recent <c>When</c> wrote.</summary>
    internal IReadOnlySet<string> Written => _written;

    /// <summary>The document ids the most recent <c>When</c> deleted.</summary>
    internal IReadOnlySet<string> Removed => _removed;

    internal void ForTenant(string? tenantId) => _tenantId = tenantId;

    internal void At(DateTimeOffset timestamp) => _timestamp = timestamp;

    internal void AtPosition(long position)
    {
        if (position < 1)
            throw new SpecificationException(
                $"Positions in the log start at 1, so {position} is not one an event could have had.");

        _position = position - 1;
    }

    internal void WithMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
    }

    /// <summary>Folds history on: the same fold as <see cref="Act"/>, without re-basing.</summary>
    internal void Arrange(IEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var @event in events)
            Project(@event);
    }

    /// <summary>
    /// Folds the events under test on, after noting how the documents stood — which is what
    /// <c>ThenUnchanged</c> and <c>ThenDeleted</c> compare against.
    /// </summary>
    internal void Act(IEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);

        _before = new Dictionary<string, TState>(_documents, StringComparer.Ordinal);
        _written.Clear();
        _removed.Clear();

        foreach (var @event in events)
            Project(@event);
    }

    /// <summary>
    /// Folds one event, mirroring what <c>DeclaredAsyncProjection</c> does per event minus the
    /// state store: skip what is not handled, skip what routes nowhere, apply, record the write.
    /// </summary>
    private void Project(IEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (EventTypeAttribute.GetEventType(@event.GetType()) is null)
            throw new SpecificationException(
                $"{@event.GetType().Name} carries no [EventType] attribute, so it could never be " +
                "appended to the log and could never reach a projection.");

        if (!_declaration.Handles(@event)) return;

        var documentId = _declaration.GetDocumentId(@event);
        if (documentId is null) return;

        var position = ++_position;

        var state = _documents.TryGetValue(documentId, out var existing)
            ? existing
            : _declaration.InitialState();

        if (state is IProjectionEntity entity && entity.LastProcessedPosition >= position)
            return;

        var context = new ProjectionContext
        {
            EventId = EventIdFor(position),
            Position = position,
            Timestamp = _timestamp,
            TenantId = _tenantId,
            Metadata = _metadata
        };

        switch (_declaration.Apply(state, @event, context))
        {
            case ProjectionResult<TState>.Set set:
                if (set.State is IProjectionEntity written) written.LastProcessedPosition = position;
                _documents[documentId] = set.State;
                _written.Add(documentId);
                _removed.Remove(documentId);
                break;

            case ProjectionResult<TState>.Delete:
                _documents.Remove(documentId);
                _removed.Add(documentId);
                _written.Remove(documentId);
                break;
        }
    }

    /// <summary>
    /// A distinct event ID per position, derived from it rather than drawn at random so that two
    /// runs of the same specification are the same run.
    /// </summary>
    private static Guid EventIdFor(long position)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[8..], position);
        return new Guid(bytes);
    }
}
