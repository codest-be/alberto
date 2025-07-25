using EventStore.Events;

namespace EventStore;

/// <summary>
/// Represents a query to filter events by event tags and event types
/// </summary>
public sealed class StreamQuery(
    IEnumerable<EventTag> tags = null!,
    IEnumerable<EventType> eventTypes = null!,
    bool requireAllTags = false,
    bool requireAllEventTypes = false)
{
    /// <summary>
    /// Tags to filter by (can be empty for all)
    /// </summary>
    public IReadOnlyCollection<EventTag> Tags { get; } = tags?.ToList() ?? [];

    /// <summary>
    /// Event types to filter by (can be empty for all)
    /// </summary>
    public IReadOnlyCollection<EventType> EventTypes { get; } = eventTypes?.ToList() ?? [];

    /// <summary>
    /// Whether all event tags must be present (AND) or any can be present (OR)
    /// </summary>
    public bool RequireAllTags { get; } = requireAllTags;

    /// <summary>
    /// Whether all event types must be present (AND) or any can be present (OR)
    /// </summary>
    public bool RequireAllEventTypes { get; } = requireAllEventTypes;

    /// <summary>
    /// Creates a new StreamQuery with additional event tags
    /// </summary>
    public StreamQuery WithTags(params IEnumerable<EventTag> tags)
    {
        var combinedIdentifiers = new List<EventTag>(Tags);
        combinedIdentifiers.AddRange(tags);

        return new StreamQuery(
            combinedIdentifiers,
            EventTypes,
            RequireAllTags,
            RequireAllEventTypes);
    }

    /// <summary>
    /// Creates a new StreamQuery with additional event types
    /// </summary>
    public StreamQuery WithEventTypes(params IEnumerable<EventType> additionalEventTypes)
    {
        var combinedEventTypes = new List<EventType>(EventTypes);
        combinedEventTypes.AddRange(additionalEventTypes);

        return new StreamQuery(
            Tags,
            combinedEventTypes,
            RequireAllTags,
            RequireAllEventTypes);
    }

    /// <summary>
    /// Creates a new StreamQuery with additional event types
    /// </summary>
    public StreamQuery WithEventTypes(params IEnumerable<Type> additionalEventTypes)
        => WithEventTypes(additionalEventTypes.Select(e => EventType.GetEventType(e)!));

    /// <summary>
    /// Creates a new StreamQuery that requires all event tags to be present
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
    /// Creates a new StreamQuery that requires all event types to be present
    /// </summary>
    public StreamQuery RequiringAllEventTypes()
    {
        return new StreamQuery(
            Tags,
            EventTypes,
            RequireAllTags,
            true);
    }
    
    public override string ToString()
    {
        var parts = new List<string>();

        // Add event tags part
        if (Tags.Any())
        {
            var identifierValues = string.Join(",", Tags.Select(d => $"'{d}'"));
            var tagClause = $"tag in [{identifierValues}]";
            parts.Add(tagClause);
        }

        // Add event types part
        if (EventTypes.Any())
        {
            var eventTypeValues = string.Join(",", EventTypes.Select(e => $"'{e}'"));
            var eventTypesClause = $"event type in [{eventTypeValues}]";
            parts.Add(eventTypesClause);
        }

        // If no conditions, return a wildcard
        if (!parts.Any())
        {
            return "*";
        }

        // Join parts with the appropriate operator
        if (parts.Count == 1)
        {
            return parts[0];
        }

        // Determine operator based on requirements
        var operatorSymbol = DetermineOperator();
        return string.Join($" {operatorSymbol} ", parts);
    }
    
    private string DetermineOperator()
    {
        // If both event tags and event types exist, we need to determine the operator
        if (Tags.Any() && EventTypes.Any())
        {
            // If either requires ALL, use AND (more restrictive)
            if (RequireAllTags || RequireAllEventTypes)
            {
                return "AND";
            }
            return "OR";
        }

        // If only one type exists, the operator doesn't matter for display
        // but we'll show AND if that type requires all
        if (Tags.Any() && RequireAllTags)
        {
            return "AND";
        }
        if (EventTypes.Any() && RequireAllEventTypes)
        {
            return "AND";
        }

        return "OR";
    }
}