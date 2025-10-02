using System.Text.RegularExpressions;

namespace Alberto.EventStore.Events;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public partial class EventType : Attribute
{
    public EventType(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));

        Regex regex = EventTypeRegex();
        if (!regex.IsMatch(id))
            throw new ArgumentException($"Event type {id} is not valid: can only contain a-z and '-'");

        Id = id;
    }

    public string Id { get; }

    public static EventType? GetEventType(Type type)
    {
        IEnumerable<EventType> attributes = type.GetCustomAttributes(typeof(EventType), false).Cast<EventType>();
        string? eventType = attributes.FirstOrDefault()?.Id;

        if (eventType == null) return null;
        return new EventType(eventType);
    }

    [GeneratedRegex("^[a-z-]+$")]
    private static partial Regex EventTypeRegex();
}