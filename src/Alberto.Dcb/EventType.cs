using System.Text.RegularExpressions;

namespace Alberto.Dcb;

/// <summary>
/// Attribute to specify the event type identifier for an event class.
/// The event type is used for filtering and routing events.
/// </summary>
/// <example>
/// <code>
/// [EventType("order-placed")]
/// public record OrderPlaced(Guid OrderId, decimal Amount) : IEvent;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed partial class EventTypeAttribute : Attribute
{
    public EventTypeAttribute(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Event type ID cannot be null or whitespace.", nameof(id));

        if (!EventTypeRegex().IsMatch(id))
            throw new ArgumentException(
                $"Event type '{id}' is invalid. Only lowercase letters, numbers, hyphens, and underscores are allowed.",
                nameof(id));

        Id = id;
    }

    /// <summary>
    /// The unique identifier for this event type (e.g., "order-placed", "customer_created").
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the event type attribute from a type, if present.
    /// </summary>
    public static EventTypeAttribute? GetEventType(Type type)
    {
        var attribute = type
            .GetCustomAttributes(typeof(EventTypeAttribute), false)
            .Cast<EventTypeAttribute>()
            .FirstOrDefault();

        return attribute;
    }

    /// <summary>
    /// Gets the event type ID from a type, throwing if not found.
    /// </summary>
    public static string GetEventTypeId(Type type)
    {
        var attribute = GetEventType(type);
        if (attribute is null)
            throw new InvalidOperationException(
                $"Type '{type.FullName}' does not have an [EventType] attribute.");

        return attribute.Id;
    }

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex EventTypeRegex();
}

/// <summary>
/// Represents an event type identifier as a value type.
/// Used in queries and event metadata.
/// </summary>
public readonly struct EventType : IEquatable<EventType>
{
    public EventType(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Event type ID cannot be null or whitespace.", nameof(id));

        Id = id;
    }

    /// <summary>
    /// The event type identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Creates an EventType from a CLR type that has the [EventType] attribute.
    /// </summary>
    public static EventType FromType<T>() where T : IEvent
        => FromType(typeof(T));

    /// <summary>
    /// Creates an EventType from a CLR type that has the [EventType] attribute.
    /// </summary>
    public static EventType FromType(Type type)
        => new(EventTypeAttribute.GetEventTypeId(type));

    public bool Equals(EventType other) => string.Equals(Id, other.Id, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is EventType other && Equals(other);
    public override int GetHashCode() => Id?.GetHashCode() ?? 0;
    public override string ToString() => Id;

    public static bool operator ==(EventType left, EventType right) => left.Equals(right);
    public static bool operator !=(EventType left, EventType right) => !left.Equals(right);

    public static implicit operator string(EventType eventType) => eventType.Id;
    public static implicit operator EventType(string id) => new(id);
}
