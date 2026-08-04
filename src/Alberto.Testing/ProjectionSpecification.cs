using Alberto.Subscriptions;

namespace Alberto.Testing;

/// <summary>
/// A specification over a <see cref="ProjectionDeclaration{TState}"/>: events in, documents out.
/// Start one with <see cref="ProjectionSpec.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Events are folded exactly as the runtime folds them. An event the projection declares no
/// handler for passes through without effect, one whose <c>id</c> selector returns
/// <see langword="null"/> is skipped, <c>Unchanged</c> leaves the document alone, <c>Delete</c>
/// removes it, and a state implementing <see cref="IProjectionEntity"/> gets its
/// <see cref="IProjectionEntity.LastProcessedPosition"/> stamped and is skipped when an event
/// arrives at or below it.
/// </para>
/// <para>
/// One document set, not one per tenant. The runtime keeps a state store per tenant and that
/// store is what isolates them, so tenancy here is context and nothing more: <see cref="ForTenant"/>
/// sets the <see cref="ProjectionContext.TenantId"/> a projection may copy into its state, and a
/// specification that means to assert isolation between two tenants is two specifications.
/// </para>
/// </remarks>
public sealed class ProjectionSpecification<TState> where TState : new()
{
    private static readonly IReadOnlyDictionary<string, string> NoMetadata =
        new Dictionary<string, string>();

    private readonly ProjectionDeclaration<TState> _declaration;
    private readonly Dictionary<string, TState> _documents = new(StringComparer.Ordinal);

    private Dictionary<string, TState> _before = new(StringComparer.Ordinal);
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    private string? _tenantId;
    private DateTimeOffset _timestamp = ProjectionSpec.Epoch;
    private IReadOnlyDictionary<string, string> _metadata = NoMetadata;
    private long _position;
    private bool _acted;

    internal ProjectionSpecification(ProjectionDeclaration<TState> declaration)
    {
        _declaration = declaration;
    }

    /// <summary>
    /// The tenant every event from here on carries, and so the
    /// <see cref="ProjectionContext.TenantId"/> the projection sees. Defaults to
    /// <see langword="null"/>, which is what a module without tenancy passes.
    /// </summary>
    public ProjectionSpecification<TState> ForTenant(string? tenantId)
    {
        _tenantId = tenantId;
        return this;
    }

    /// <summary>
    /// The timestamp every event from here on carries. Defaults to
    /// <see cref="ProjectionSpec.Epoch"/>; call this between batches when a projection records
    /// times and the test is about which one won.
    /// </summary>
    public ProjectionSpecification<TState> At(DateTimeOffset timestamp)
    {
        _timestamp = timestamp;
        return this;
    }

    /// <summary>
    /// The global position of the next event; subsequent events continue from there. Positions
    /// otherwise start at 1 and increment, which is what the log would have given them.
    /// </summary>
    /// <remarks>
    /// Rewinding is how a redelivery is written: replay an event at the position it already had
    /// and a state implementing <see cref="IProjectionEntity"/> should ignore it.
    /// </remarks>
    public ProjectionSpecification<TState> AtPosition(long position)
    {
        if (position < 1)
            throw new SpecificationException(
                $"Positions in the log start at 1, so {position} is not one an event could have had.");

        _position = position - 1;
        return this;
    }

    /// <summary>
    /// The metadata every event from here on carries. Defaults to empty.
    /// </summary>
    public ProjectionSpecification<TState> WithMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
        return this;
    }

    /// <summary>
    /// The history: the events already projected, folded into documents in the order given.
    /// Events the projection declares no handler for are ignored, exactly as they are in
    /// production — which is the usual case, since one log feeds every projection.
    /// </summary>
    /// <remarks>
    /// Repeated calls accumulate, so a shared arrange helper can hand back a partly-built
    /// specification and the test can add the events that make its case.
    /// </remarks>
    public ProjectionSpecification<TState> Given(params IEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (_acted)
            throw new SpecificationException(
                "Given(...) sets up the documents an event is projected onto, so it has to come " +
                "before When(...).");

        foreach (var @event in events)
            Project(@event);

        return this;
    }

    /// <summary>
    /// States that nothing has been projected yet. Equivalent to omitting <c>Given</c>, and there
    /// so the empty case reads as a decision rather than an oversight.
    /// </summary>
    public ProjectionSpecification<TState> GivenNoEvents() => Given();

    /// <summary>
    /// Projects the events under test onto the documents built by <c>Given</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="ThenUnchanged"/> and <see cref="ThenDeleted"/> compare against the documents as
    /// they stood before the most recent <c>When</c>, so a specification may call it more than
    /// once — replaying an event at a rewound position, say — and each call re-bases them.
    /// </remarks>
    public ProjectionSpecification<TState> When(params IEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);

        _acted = true;
        _before = new Dictionary<string, TState>(_documents, StringComparer.Ordinal);
        _written.Clear();
        _removed.Clear();

        foreach (var @event in events)
            Project(@event);

        return this;
    }

    /// <summary>
    /// Asserts that exactly one document exists, and asserts on it. The shape most projections
    /// are tested in: one order, one payment, one overview.
    /// </summary>
    public ProjectionSpecification<TState> ThenDocument(Action<TState> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        if (_documents.Count != 1)
            throw new SpecificationException(
                $"Expected exactly one document, but the projection holds {Describe(_documents)}. " +
                "Name the one you mean with ThenDocument(id, ...).");

        assert(_documents.Values.Single());
        return this;
    }

    /// <summary>Asserts that <paramref name="documentId"/> exists, and asserts on it.</summary>
    public ProjectionSpecification<TState> ThenDocument(string documentId, Action<TState> assert)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(assert);

        if (!_documents.TryGetValue(documentId, out var state))
            throw new SpecificationException(
                $"Expected a document '{documentId}', but the projection holds {Describe(_documents)}.");

        assert(state);
        return this;
    }

    /// <summary>
    /// Asserts that <paramref name="documentId"/> does not exist — never written, or written and
    /// since deleted. Use <see cref="ThenDeleted"/> when the difference is the point.
    /// </summary>
    public ProjectionSpecification<TState> ThenNoDocument(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (_documents.ContainsKey(documentId))
            throw new SpecificationException(
                $"Expected no document '{documentId}', but the projection holds one.");

        return this;
    }

    /// <summary>Asserts the projection wrote nothing at all.</summary>
    public ProjectionSpecification<TState> ThenNoDocuments()
    {
        if (_documents.Count != 0)
            throw new SpecificationException(
                $"Expected the projection to hold nothing, but it holds {Describe(_documents)}.");

        return this;
    }

    /// <summary>Asserts how many documents the projection holds.</summary>
    public ProjectionSpecification<TState> ThenDocumentCount(int expected)
    {
        if (_documents.Count != expected)
            throw new SpecificationException(
                $"Expected {expected} document(s), but the projection holds {Describe(_documents)}.");

        return this;
    }

    /// <summary>
    /// Asserts that <c>When</c> deleted <paramref name="documentId"/> — that it existed before
    /// and does not now. Distinguishes a document that was removed from one that was never
    /// written, which <see cref="ThenNoDocument"/> cannot.
    /// </summary>
    public ProjectionSpecification<TState> ThenDeleted(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        RequireActed(nameof(ThenDeleted));

        if (!_before.ContainsKey(documentId))
            throw new SpecificationException(
                $"Expected When(...) to delete '{documentId}', but no such document existed " +
                "before it. Arrange the document with Given(...) first.");

        if (_documents.ContainsKey(documentId))
            throw new SpecificationException(
                $"Expected When(...) to delete '{documentId}', but the projection still holds it.");

        return this;
    }

    /// <summary>
    /// Asserts that <c>When</c> wrote nothing — every event it carried was one this projection
    /// does not handle, or was routed to no document, or was applied to <c>Unchanged</c>.
    /// </summary>
    public ProjectionSpecification<TState> ThenUnchanged()
    {
        RequireActed(nameof(ThenUnchanged));

        if (_written.Count > 0 || _removed.Count > 0)
            throw new SpecificationException(
                "Expected When(...) to leave the projection untouched, but it wrote " +
                $"{Names(_written)} and deleted {Names(_removed)}.");

        return this;
    }

    /// <summary>
    /// The escape hatch: hands you every document, keyed by ID, for assertions the verbs above
    /// do not cover.
    /// </summary>
    public ProjectionSpecification<TState> ThenDocuments(Action<IReadOnlyDictionary<string, TState>> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(_documents);
        return this;
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

    private void RequireActed(string verb)
    {
        if (!_acted)
            throw new SpecificationException(
                $"{verb}() compares the documents against how they stood before the events under " +
                "test, so it needs a When(...) to compare across.");
    }

    private static string Describe(Dictionary<string, TState> documents) =>
        documents.Count == 0
            ? "none"
            : $"{documents.Count}: {Names(documents.Keys)}";

    private static string Names(IEnumerable<string> ids)
    {
        var listed = string.Join(", ", ids.Order(StringComparer.Ordinal).Select(id => $"'{id}'"));
        return listed.Length == 0 ? "nothing" : listed;
    }
}
