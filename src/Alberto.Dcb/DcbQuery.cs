namespace Alberto.Dcb;

/// <summary>
/// Represents a query to filter events by event types and/or tag patterns.
/// Used for both reading events and defining DCB consistency boundaries.
///
/// The query uses OR logic: matches events that have ANY of the specified types
/// OR ANY of the specified tag patterns.
/// </summary>
/// <example>
/// <code>
/// // Query by event types
/// var query = DcbQuery.ByTypes("order-placed", "order-cancelled");
///
/// // Query by exact tags
/// var query = DcbQuery.ByTags(new EventTag("order", orderId));
///
/// // Query by tag wildcard - matches all events tagged with any order
/// var query = DcbQuery.ByTagPatterns(TagPattern.Prefix("order"));
/// var query = DcbQuery.ByTagPatterns("order:*");
///
/// // Query by both (OR logic)
/// var query = DcbQuery.Empty
///     .WithTypes("order-placed")
///     .WithTags(new EventTag("customer", customerId));
/// </code>
/// </example>
public sealed class DcbQuery
{
    /// <summary>
    /// An empty query that matches all events.
    /// </summary>
    public static DcbQuery Empty { get; } = new([], []);

    /// <summary>
    /// Event types to filter by. Events matching ANY of these types are included.
    /// </summary>
    public IReadOnlyCollection<EventType> Types { get; }

    /// <summary>
    /// Tag patterns to filter by. Events matching ANY of these patterns are included.
    /// Supports both exact tags and wildcard patterns (e.g., "order:*").
    /// </summary>
    public IReadOnlyCollection<TagPattern> TagPatterns { get; }

    /// <summary>
    /// Gets exact tag matches only (excludes wildcard patterns).
    /// For backward compatibility with code expecting EventTag instances.
    /// </summary>
    public IReadOnlyCollection<EventTag> Tags =>
        TagPatterns
            .Where(p => p.IsExact)
            .Select(p => new EventTag(p.Concept, p.Id!))
            .ToArray();

    /// <summary>
    /// Gets wildcard tag patterns only (excludes exact matches).
    /// </summary>
    public IReadOnlyCollection<TagPattern> WildcardPatterns =>
        TagPatterns.Where(p => p.IsWildcard).ToArray();

    /// <summary>
    /// Returns true if any tag patterns are wildcards.
    /// </summary>
    public bool HasWildcardPatterns => TagPatterns.Any(p => p.IsWildcard);

    /// <summary>
    /// Returns true if this query has no filters (matches all events).
    /// </summary>
    public bool IsEmpty => Types.Count == 0 && TagPatterns.Count == 0;

    /// <summary>
    /// Returns true if this query filters by types only.
    /// </summary>
    public bool HasTypesOnly => Types.Count > 0 && TagPatterns.Count == 0;

    /// <summary>
    /// Returns true if this query filters by tags only.
    /// </summary>
    public bool HasTagsOnly => TagPatterns.Count > 0 && Types.Count == 0;

    /// <summary>
    /// Returns true if this query filters by both types and tags.
    /// </summary>
    public bool HasTypesAndTags => Types.Count > 0 && TagPatterns.Count > 0;

    private DcbQuery(IReadOnlyCollection<EventType> types, IReadOnlyCollection<TagPattern> tagPatterns)
    {
        Types = types;
        TagPatterns = tagPatterns;
    }

    /// <summary>
    /// Creates a query that filters by event types.
    /// </summary>
    public static DcbQuery ByTypes(params EventType[] types)
        => new(types, []);

    /// <summary>
    /// Creates a query that filters by event types (string overload).
    /// </summary>
    public static DcbQuery ByTypes(params string[] typeIds)
        => new(typeIds.Select(id => new EventType(id)).ToArray(), []);

    /// <summary>
    /// Creates a query that filters by event types from CLR types.
    /// </summary>
    public static DcbQuery ByTypes(params Type[] eventTypes)
        => new(eventTypes.Select(EventType.FromType).ToArray(), []);

    /// <summary>
    /// Creates a query that filters by exact tags.
    /// </summary>
    public static DcbQuery ByTags(params EventTag[] tags)
        => new([], tags.Select(TagPattern.Exact).ToArray());

    /// <summary>
    /// Creates a query that filters by exact tags (string overload).
    /// </summary>
    public static DcbQuery ByTags(params string[] tags)
        => new([], tags.Select(t => TagPattern.Exact(EventTag.Parse(t))).ToArray());

    /// <summary>
    /// Creates a query that filters by tag patterns (supports wildcards).
    /// </summary>
    public static DcbQuery ByTagPatterns(params TagPattern[] patterns)
        => new([], patterns);

    /// <summary>
    /// Creates a query that filters by tag patterns (string overload, supports wildcards).
    /// Use "concept:id" for exact match or "concept:*" for wildcard.
    /// </summary>
    public static DcbQuery ByTagPatterns(params string[] patterns)
        => new([], patterns.Select(TagPattern.Parse).ToArray());

    /// <summary>
    /// Returns a new query with additional event types.
    /// </summary>
    public DcbQuery WithTypes(params EventType[] types)
        => new([..Types, ..types], TagPatterns);

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
    /// Returns a new query with additional exact tag matches.
    /// </summary>
    public DcbQuery WithTags(params EventTag[] tags)
        => new(Types, [..TagPatterns, ..tags.Select(TagPattern.Exact)]);

    /// <summary>
    /// Returns a new query with additional exact tag matches (string overload).
    /// </summary>
    public DcbQuery WithTags(params string[] tags)
        => WithTags(tags.Select(EventTag.Parse).ToArray());

    /// <summary>
    /// Returns a new query with an additional exact tag match.
    /// </summary>
    public DcbQuery WithTag(string concept, string id)
        => WithTags(new EventTag(concept, id));

    /// <summary>
    /// Returns a new query with additional tag patterns (supports wildcards).
    /// </summary>
    public DcbQuery WithTagPatterns(params TagPattern[] patterns)
        => new(Types, [..TagPatterns, ..patterns]);

    /// <summary>
    /// Returns a new query with additional tag patterns (string overload, supports wildcards).
    /// Use "concept:id" for exact match or "concept:*" for wildcard.
    /// </summary>
    public DcbQuery WithTagPatterns(params string[] patterns)
        => WithTagPatterns(patterns.Select(TagPattern.Parse).ToArray());

    /// <summary>
    /// Returns a new query with a wildcard pattern for all tags of a given concept.
    /// </summary>
    public DcbQuery WithTagPrefix(string concept)
        => WithTagPatterns(TagPattern.Prefix(concept));

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

        if (TagPatterns.Count > 0)
        {
            var patternValues = string.Join(", ", TagPatterns.Select(p => $"'{p.Value}'"));
            parts.Add($"tags=[{patternValues}]");
        }

        return string.Join(" OR ", parts);
    }
}
