using EventStore.Events;

namespace EventStore;

/// <summary>
/// Represents a query to filter events by domain identifiers and event types
/// </summary>
public class StreamQuery(
    IEnumerable<DomainId> domainIdentifiers = null!,
    IEnumerable<EventType> eventTypes = null!,
    bool requireAllDomainIdentifiers = false,
    bool requireAllEventTypes = false)
{
    /// <summary>
    /// Domain identifiers to filter by (can be empty for all)
    /// </summary>
    private IReadOnlyCollection<DomainId> DomainIdentifiers { get; } = domainIdentifiers?.ToList() ?? [];

    /// <summary>
    /// Event types to filter by (can be empty for all)
    /// </summary>
    private IReadOnlyCollection<EventType> EventTypes { get; } = eventTypes?.ToList() ?? [];

    /// <summary>
    /// Whether all domain identifiers must be present (AND) or any can be present (OR)
    /// </summary>
    private bool RequireAllDomainIdentifiers { get; } = requireAllDomainIdentifiers;

    /// <summary>
    /// Whether all event types must be present (AND) or any can be present (OR)
    /// </summary>
    private bool RequireAllEventTypes { get; } = requireAllEventTypes;

    /// <summary>
    /// Creates a new StreamQuery with additional domain identifiers
    /// </summary>
    public StreamQuery WithDomainIdentifiers(params IEnumerable<DomainId> additionalIdentifiers)
    {
        var combinedIdentifiers = new List<DomainId>(DomainIdentifiers);
        combinedIdentifiers.AddRange(additionalIdentifiers);

        return new StreamQuery(
            combinedIdentifiers,
            EventTypes,
            RequireAllDomainIdentifiers,
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
            DomainIdentifiers,
            combinedEventTypes,
            RequireAllDomainIdentifiers,
            RequireAllEventTypes);
    }

    /// <summary>
    /// Creates a new StreamQuery with additional event types
    /// </summary>
    public StreamQuery WithEventTypes(params IEnumerable<Type> additionalEventTypes)
        => WithEventTypes(additionalEventTypes.Select(e => EventType.GetEventType(e)!));

    /// <summary>
    /// Creates a new StreamQuery that requires all domain identifiers to be present
    /// </summary>
    public StreamQuery RequiringAllDomainIdentifiers()
    {
        return new StreamQuery(
            DomainIdentifiers,
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
            DomainIdentifiers,
            EventTypes,
            RequireAllDomainIdentifiers,
            true);
    }
    
    public override string ToString()
    {
        var parts = new List<string>();

        // Add domain identifiers part
        if (DomainIdentifiers.Any())
        {
            var identifierValues = string.Join(",", DomainIdentifiers.Select(d => $"'{d}'"));
            var domainClause = $"domain identifier in [{identifierValues}]";
            parts.Add(domainClause);
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
        // If both domain identifiers and event types exist, we need to determine the operator
        if (DomainIdentifiers.Any() && EventTypes.Any())
        {
            // If either requires ALL, use AND (more restrictive)
            if (RequireAllDomainIdentifiers || RequireAllEventTypes)
            {
                return "AND";
            }
            return "OR";
        }

        // If only one type exists, the operator doesn't matter for display
        // but we'll show AND if that type requires all
        if (DomainIdentifiers.Any() && RequireAllDomainIdentifiers)
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