using System.Collections.Immutable;

namespace Alberto.Dcb;

public enum TagMatchMode
{
    Any,
    All
}

/// <summary>
/// Controls how the type axis and the tag axis combine when both are specified
/// in a <see cref="DcbQuery"/>.
/// </summary>
public enum CompositionMode
{
    /// <summary>
    /// Default. When both types and tags are specified, an event must match BOTH a type
    /// and a tag pattern to be included. This is the natural reading for state-folding and
    /// for DCB consistency boundaries scoped to a particular aggregate ("events of these
    /// types <i>for this entity</i>").
    /// </summary>
    Intersect,

    /// <summary>
    /// Legacy / heterogeneous mode. When both types and tags are specified, an event matches
    /// if it has any of the types OR any of the tag patterns. Useful only when intentionally
    /// widening a boundary to include unrelated events (e.g. "any of these global types,
    /// regardless of tag, plus any event for this aggregate"). Opt in explicitly via
    /// <see cref="DcbQuery.AsUnion"/>.
    /// </summary>
    Union,
}

/// <summary>
/// Represents a query to filter events by event types and/or tags.
/// Used for both reading events and defining DCB consistency boundaries.
///
/// Composition rules:
/// <list type="bullet">
/// <item>Multiple types are always OR'd: an event matches if it has ANY of the listed types.</item>
/// <item>Multiple tags OR by default; <see cref="ByAllTags(EventTag[])"/> requires ALL.</item>
/// <item>When both types and tags are specified, the default is <see cref="CompositionMode.Intersect"/>:
///       an event must match the type axis AND the tag axis. Use <see cref="AsUnion"/> for the
///       legacy OR-across-axes behavior.</item>
/// </list>
/// </summary>
/// <remarks>
/// Tags match exactly, on the whole <c>concept:id</c> pair. There is deliberately no way to
/// query a concept as a whole ("every event tagged with any order"): a boundary that wide is
/// not a query this store supports, and supporting it cost an index on every tag written.
/// A query is always scoped to the entities it names.
/// </remarks>
/// <example>
/// <code>
/// // Query by event types
/// var query = DcbQuery.ByTypes("order-placed", "order-cancelled");
///
/// // Query by tags
/// var query = DcbQuery.ByTags(new EventTag("order", orderId));
///
/// // Narrow: events of these types AND tagged with this order (Intersect, the default)
/// var query = DcbQuery.For("order", orderId).WithType&lt;OrderPlaced&gt;();
///
/// // Widen: events of these types OR tagged with this order (Union, opt-in)
/// var query = DcbQuery.For("order", orderId).WithType&lt;OrderPlaced&gt;().AsUnion();
/// </code>
/// </example>
public sealed class DcbQuery
{
    /// <summary>
    /// An empty query that matches all events.
    /// </summary>
    public static DcbQuery Empty { get; } = new([], [], TagMatchMode.Any, CompositionMode.Intersect);

    /// <summary>
    /// Event types to filter by. Events matching ANY of these types are included.
    /// </summary>
    public IReadOnlyCollection<EventType> Types { get; }

    /// <summary>
    /// Tags to filter by. Events carrying ANY of these tags are included, unless
    /// <see cref="TagMatchMode"/> is <see cref="TagMatchMode.All"/>.
    /// </summary>
    public IReadOnlyCollection<EventTag> Tags { get; }

    /// <summary>
    /// Controls whether tag queries match ANY tag or require ALL specified tags.
    /// </summary>
    public TagMatchMode TagMatchMode { get; }

    /// <summary>
    /// Controls how the type axis combines with the tag axis when both are specified.
    /// Defaults to <see cref="CompositionMode.Intersect"/> (AND).
    /// </summary>
    public CompositionMode CompositionMode { get; }

    /// <summary>
    /// Returns true if this query requires an event to match all specified tags.
    /// </summary>
    public bool RequiresAllTags => TagMatchMode == TagMatchMode.All;

    /// <summary>
    /// Returns true if this query has no filters (matches all events).
    /// </summary>
    public bool IsEmpty => Types.Count == 0 && Tags.Count == 0;

    /// <summary>
    /// Returns true if this query filters by types only.
    /// </summary>
    public bool HasTypesOnly => Types.Count > 0 && Tags.Count == 0;

    /// <summary>
    /// Returns true if this query filters by tags only.
    /// </summary>
    public bool HasTagsOnly => Tags.Count > 0 && Types.Count == 0;

    /// <summary>
    /// Returns true if this query filters by both types and tags.
    /// </summary>
    /// <remarks>
    /// Completes the shape partition with <see cref="HasTypesOnly"/> and <see cref="HasTagsOnly"/>.
    /// Backends do not route on this predicate directly — once both axes are present the routing
    /// decision depends on <see cref="CompositionMode"/>, so they branch on
    /// <see cref="IntersectsTypesAndTags"/> or <see cref="UnionsTypesAndTags"/> instead. This is
    /// the shared precondition those two are built from, and it is public so consumers can ask
    /// the shape question on its own.
    /// </remarks>
    public bool HasTypesAndTags => Types.Count > 0 && Tags.Count > 0;

    /// <summary>
    /// Returns true when both axes are present and are combined with AND semantics
    /// (the default). Backends route to intersection-aware functions when this is true.
    /// </summary>
    public bool IntersectsTypesAndTags =>
        HasTypesAndTags && CompositionMode == CompositionMode.Intersect;

    /// <summary>
    /// Returns true when both axes are present and are combined with OR semantics.
    /// </summary>
    /// <remarks>
    /// The complement of <see cref="IntersectsTypesAndTags"/>, provided for authors implementing
    /// <see cref="IEventStoreBackend"/> against a store of their own. The in-tree Postgres and
    /// in-memory backends branch only on the intersect side and let union fall through to their
    /// default OR path, so this predicate has no call site in this repository — that is by
    /// design, not an oversight. Deliberately kept so the pair is symmetric and a backend can
    /// recognise <see cref="CompositionMode.Union"/> without reaching for
    /// <see cref="CompositionMode"/> itself.
    /// </remarks>
    public bool UnionsTypesAndTags =>
        HasTypesAndTags && CompositionMode == CompositionMode.Union;

    private DcbQuery(
        IReadOnlyCollection<EventType> types,
        IReadOnlyCollection<EventTag> tags,
        TagMatchMode tagMatchMode,
        CompositionMode compositionMode)
    {
        Types = types.ToImmutableArray();
        Tags = tags.ToImmutableArray();
        TagMatchMode = tagMatchMode;
        CompositionMode = compositionMode;
    }

    /// <summary>
    /// Creates a query that filters by event types.
    /// </summary>
    public static DcbQuery ByTypes(params EventType[] types)
        => new(types, [], TagMatchMode.Any, CompositionMode.Intersect);

    /// <summary>
    /// Creates a query that filters by event types (string overload).
    /// </summary>
    public static DcbQuery ByTypes(params string[] typeIds)
        => new(typeIds.Select(id => new EventType(id)).ToArray(), [], TagMatchMode.Any, CompositionMode.Intersect);

    /// <summary>
    /// Creates a query that filters by event types from CLR types.
    /// </summary>
    public static DcbQuery ByTypes(params Type[] eventTypes)
        => new(eventTypes.Select(EventType.FromType).ToArray(), [], TagMatchMode.Any, CompositionMode.Intersect);

    /// <summary>
    /// Creates a query that filters by tags.
    /// </summary>
    public static DcbQuery ByTags(params EventTag[] tags)
        => new([], tags, TagMatchMode.Any, CompositionMode.Intersect);

    /// <summary>
    /// Creates a query that filters by tags (string overload). Each tag must be a full
    /// <c>concept:id</c> pair.
    /// </summary>
    public static DcbQuery ByTags(params string[] tags)
        => new([], tags.Select(EventTag.Parse).ToArray(), TagMatchMode.Any, CompositionMode.Intersect);

    /// <summary>
    /// Creates a query that requires events to match all specified tags.
    /// </summary>
    public static DcbQuery ByAllTags(params EventTag[] tags)
        => new([], tags, TagMatchMode.All, CompositionMode.Intersect);

    /// <summary>
    /// Creates a query that requires events to match all specified tags (string overload).
    /// </summary>
    public static DcbQuery ByAllTags(params string[] tags)
        => new([], tags.Select(EventTag.Parse).ToArray(), TagMatchMode.All, CompositionMode.Intersect);

    /// <summary>
    /// Creates a query for a single concept:id tag — the most common pattern.
    /// Equivalent to <see cref="ByTags(EventTag[])"/> with a single tag.
    /// </summary>
    /// <example>
    /// <code>
    /// var q = DcbQuery.For("order", orderId);
    /// var q = DcbQuery.For("order", orderId).WithTypes("order-placed");
    /// </code>
    /// </example>
    public static DcbQuery For(string concept, string id)
        => ByTags(new EventTag(concept, id));

    /// <summary>
    /// Creates a query for a single concept:id tag using a Guid ID.
    /// </summary>
    public static DcbQuery For(string concept, Guid id)
        => ByTags(new EventTag(concept, id.ToString()));

    /// <summary>
    /// Creates a query for a single concept:id tag using an int ID.
    /// </summary>
    public static DcbQuery For(string concept, int id)
        => ByTags(new EventTag(concept, id.ToString()));

    /// <summary>
    /// Creates a query for a single concept:id tag using a long ID.
    /// </summary>
    public static DcbQuery For(string concept, long id)
        => ByTags(new EventTag(concept, id.ToString()));

    /// <summary>
    /// Creates a query for a single concept:id tag. Calls ToString() on the id.
    /// </summary>
    public static DcbQuery For<TId>(string concept, TId id) where TId : notnull
        => ByTags(new EventTag(concept, id.ToString()!));

    /// <summary>
    /// Returns a new query with additional event types.
    /// </summary>
    public DcbQuery WithTypes(params EventType[] types)
        => new([..Types, ..types], Tags, TagMatchMode, CompositionMode);

    /// <summary>
    /// Returns a new query with additional event types (string overload).
    /// </summary>
    public DcbQuery WithTypes(params string[] typeIds)
        => WithTypes(typeIds.Select(id => new EventType(id)).ToArray());

    /// <summary>
    /// Returns a new query with an additional event type from a CLR type.
    /// </summary>
    public DcbQuery WithType<TEvent>() where TEvent : IEvent
        => WithTypes(EventType.FromType<TEvent>());

    /// <summary>
    /// Returns a new query with additional tag matches.
    /// </summary>
    public DcbQuery WithTags(params EventTag[] tags)
        => new(Types, [..Tags, ..tags], TagMatchMode, CompositionMode);

    /// <summary>
    /// Returns a new query with additional tag matches (string overload).
    /// </summary>
    public DcbQuery WithTags(params string[] tags)
        => WithTags(tags.Select(EventTag.Parse).ToArray());

    /// <summary>
    /// Returns a new query with an additional tag match.
    /// </summary>
    public DcbQuery WithTag(string concept, string id)
        => WithTags(new EventTag(concept, id));

    /// <summary>
    /// Returns a new query whose type axis and tag axis combine with OR semantics
    /// (the legacy behavior). Use only when intentionally widening the boundary to
    /// include unrelated events.
    /// </summary>
    public DcbQuery AsUnion() =>
        CompositionMode == CompositionMode.Union
            ? this
            : new DcbQuery(Types, Tags, TagMatchMode, CompositionMode.Union);

    /// <summary>
    /// Returns a new query whose type axis and tag axis combine with AND semantics
    /// (the default). Useful for explicitly converting back from <see cref="AsUnion"/>.
    /// </summary>
    public DcbQuery AsIntersect() =>
        CompositionMode == CompositionMode.Intersect
            ? this
            : new DcbQuery(Types, Tags, TagMatchMode, CompositionMode.Intersect);

    /// <summary>
    /// Returns a string representation of the query for debugging and logging.
    /// </summary>
    public override string ToString()
    {
        if (IsEmpty)
            return "*";

        var parts = new List<string>();

        if (Types.Count > 0)
        {
            var typeValues = string.Join(", ", Types.Select(t => $"'{t.Id}'"));
            parts.Add($"types=[{typeValues}]");
        }

        if (Tags.Count > 0)
        {
            var tagValues = string.Join(", ", Tags.Select(t => $"'{t.Value}'"));
            parts.Add(RequiresAllTags ? $"tags(all)=[{tagValues}]" : $"tags=[{tagValues}]");
        }

        var separator = HasTypesAndTags && CompositionMode == CompositionMode.Intersect ? " AND " : " OR ";
        return string.Join(separator, parts);
    }
}
