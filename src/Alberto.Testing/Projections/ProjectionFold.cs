using Alberto.Projections;

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

    private readonly ProjectionChanges<TState> _changes;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    private Dictionary<string, TState> _before = new(StringComparer.Ordinal);
    private string? _tenantId;
    private DateTimeOffset _timestamp = ProjectionSpec.Epoch;
    private IReadOnlyDictionary<string, string> _metadata = NoMetadata;
    private long _position;

    internal ProjectionFold(ProjectionDeclaration<TState> declaration)
    {
        _declaration = declaration;
        _changes = new ProjectionChanges<TState>(declaration);
    }

    /// <summary>The documents as they stand.</summary>
    internal IReadOnlyDictionary<string, TState> Documents => _changes.Upserts;

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

        _before = new Dictionary<string, TState>(_changes.Upserts, StringComparer.Ordinal);
        _written.Clear();
        _removed.Clear();

        foreach (var @event in events)
            Project(@event);
    }

    /// <summary>
    /// Folds one event through the same step the runtime hosts use, minus the state store: the
    /// documents already held are the changes so far, so nothing is loaded. Skip what is not
    /// handled, skip what routes nowhere, apply, record the write.
    /// </summary>
    private void Project(IEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (EventTypeAttribute.GetEventType(@event.GetType()) is null)
            throw new SpecificationException(
                $"{@event.GetType().Name} carries no [EventType] attribute, so it could never be " +
                "appended to the log and could never reach a projection.");

        if (!_declaration.Handles(@event)) return;

        var handler = _declaration.Handlers[EventTypeAttribute.GetEventTypeId(@event.GetType())];
        var documentId = handler.GetDocumentId(@event);
        if (documentId is null) return;

        var position = ++_position;

        var context = new ProjectionContext
        {
            EventId = EventIdFor(position),
            Position = position,
            Timestamp = _timestamp,
            TenantId = _tenantId,
            Metadata = _metadata
        };

        switch (_changes.Fold(handler, documentId, @event, context))
        {
            case ProjectionResult<TState>.Set:
                _written.Add(documentId);
                _removed.Remove(documentId);
                break;

            case ProjectionResult<TState>.Delete:
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
