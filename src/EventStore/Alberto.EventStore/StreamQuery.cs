using Alberto.EventStore.Events;

namespace Alberto.EventStore;

/// <summary>
///     Represents a query to filter events by event tags and event types
/// </summary>
public sealed class StreamQuery(
    IEnumerable<EventTag> tags = null!,
    IEnumerable<EventType> eventTypes = null!,
    bool requireAllTags = false,
    bool requireAllEventTypes = false)
{
    /// <summary>
    ///     Tags to filter by (can be empty for all)
    /// </summary>
    public IReadOnlyCollection<EventTag> Tags { get; } = tags?.ToList() ?? [];

    /// <summary>
    ///     Event types to filter by (can be empty for all)
    /// </summary>
    public IReadOnlyCollection<EventType> EventTypes { get; } = eventTypes?.ToList() ?? [];

    /// <summary>
    ///     Whether all event tags must be present (AND) or any can be present (OR)
    /// </summary>
    public bool RequireAllTags { get; } = requireAllTags;

    /// <summary>
    ///     Whether all event types must be present (AND) or any can be present (OR)
    /// </summary>
    public bool RequireAllEventTypes { get; } = requireAllEventTypes;

    /// <summary>
    ///     Creates a new StreamQuery with additional event tags
    /// </summary>
    public StreamQuery WithTags(params EventTag[] tags)
    {
        List<EventTag> combinedIdentifiers = new(Tags);
        combinedIdentifiers.AddRange(tags);

        return new StreamQuery(
            combinedIdentifiers,
            EventTypes,
            RequireAllTags,
            RequireAllEventTypes);
    }

    /// <summary>
    ///     Creates a new StreamQuery with additional event types
    /// </summary>
    public StreamQuery WithEventTypes(params EventType[] additionalEventTypes)
    {
        List<EventType> combinedEventTypes = new(EventTypes);
        combinedEventTypes.AddRange(additionalEventTypes);

        return new StreamQuery(
            Tags,
            combinedEventTypes,
            RequireAllTags,
            RequireAllEventTypes);
    }

    /// <summary>
    ///     Creates a new StreamQuery with additional event types
    /// </summary>
    public StreamQuery WithEventTypes(params Type[] additionalEventTypes)
    {
        return WithEventTypes(additionalEventTypes.Select(e => EventType.GetEventType(e)!).ToArray());
    }

    /// <summary>
    ///     Creates a new StreamQuery with an additional event type using a generic type parameter
    /// </summary>
    public StreamQuery WithEventType<TEvent>()
    {
        EventType? eventType = EventType.GetEventType(typeof(TEvent));
        if (eventType == null)
            throw new InvalidOperationException($"Type {typeof(TEvent).Name} does not have an EventType attribute");

        return WithEventTypes(eventType);
    }

    /// <summary>
    ///     Creates a new StreamQuery that requires all event tags to be present
    /// </summary>
    public StreamQuery RequiringAllTags()
    {
        return new StreamQuery(
            Tags,
            EventTypes,
            true,
            RequireAllEventTypes);
    }

    /// <summary>
    ///     Creates a new StreamQuery that requires all event types to be present
    /// </summary>
    public StreamQuery RequiringAllEventTypes()
    {
        return new StreamQuery(
            Tags,
            EventTypes,
            RequireAllTags,
            true);
    }

    /// <summary>
    /// Returns a string representation of the query for debugging and logging.
    /// </summary>
    public override string ToString()
    {
        List<string> parts = [];

        if (Tags.Any())
        {
            string identifierValues = string.Join(",", Tags.Select(d => $"'{d}'"));
            string tagClause = $"tag in [{identifierValues}]";
            parts.Add(tagClause);
        }

        if (EventTypes.Any())
        {
            string eventTypeValues = string.Join(",", EventTypes.Select(e => $"'{e.Id}'"));
            string eventTypesClause = $"event type in [{eventTypeValues}]";
            parts.Add(eventTypesClause);
        }

        if (!parts.Any()) return "*";
        if (parts.Count == 1) return parts[0];

        string operatorSymbol = DetermineOperator();
        return string.Join($" {operatorSymbol} ", parts);
    }

    private string DetermineOperator()
    {
        if (Tags.Any() && EventTypes.Any())
        {
            if (RequireAllTags || RequireAllEventTypes) return "AND";
            return "OR";
        }

        if (Tags.Any() && RequireAllTags) return "AND";
        if (EventTypes.Any() && RequireAllEventTypes) return "AND";

        return "OR";
    }
}