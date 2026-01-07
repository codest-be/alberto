namespace Alberto.Dcb;

/// <summary>
/// Represents a query to filter events by event types and/or tags.
/// Used for both reading events and defining DCB consistency boundaries.
///
/// The query uses OR logic: matches events that have ANY of the specified types
/// OR ANY of the specified tags.
/// </summary>
/// <example>
/// <code>
/// // Query by event types
/// var query = DcbQuery.ByTypes("order-placed", "order-cancelled");
///
/// // Query by tags
/// var query = DcbQuery.ByTags(new EventTag("order", orderId));
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
    /// Tags to filter by. Events matching ANY of these tags are included.
    /// </summary>
    public IReadOnlyCollection<EventTag> Tags { get; }

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
    public bool HasTypesAndTags => Types.Count > 0 && Tags.Count > 0;

    private DcbQuery(IReadOnlyCollection<EventType> types, IReadOnlyCollection<EventTag> tags)
    {
        Types = types;
        Tags = tags;
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
    /// Creates a query that filters by tags.
    /// </summary>
    public static DcbQuery ByTags(params EventTag[] tags)
        => new([], tags);

    /// <summary>
    /// Creates a query that filters by tags (string overload).
    /// </summary>
    public static DcbQuery ByTags(params string[] tags)
        => new([], tags.Select(EventTag.Parse).ToArray());

    /// <summary>
    /// Returns a new query with additional event types.
    /// </summary>
    public DcbQuery WithTypes(params EventType[] types)
        => new([..Types, ..types], Tags);

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
    /// Returns a new query with additional tags.
    /// </summary>
    public DcbQuery WithTags(params EventTag[] tags)
        => new(Types, [..Tags, ..tags]);

    /// <summary>
    /// Returns a new query with additional tags (string overload).
    /// </summary>
    public DcbQuery WithTags(params string[] tags)
        => WithTags(tags.Select(EventTag.Parse).ToArray());

    /// <summary>
    /// Returns a new query with an additional tag.
    /// </summary>
    public DcbQuery WithTag(string concept, string id)
        => WithTags(new EventTag(concept, id));

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
            parts.Add($"tags=[{tagValues}]");
        }

        return string.Join(" OR ", parts);
    }
}
