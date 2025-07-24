using System.Text.RegularExpressions;

namespace EventStore.Events;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public partial class EventType : Attribute
{
    public string Id { get; }

    public EventType(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));
        }

        var regex = EventTypeRegex();
        if (!regex.IsMatch(id))
        {
            throw new ArgumentException($"Event type {id} is not valid: can only contain a-z and '-'");
        }

        Id = id;
    }

    public static EventType? GetEventType(Type type)
    {
        var attributes = type.GetCustomAttributes(typeof(EventType), false).Cast<EventType>();
        var eventType = attributes.FirstOrDefault()?.Id;

        if (eventType == null) return null;
        return new EventType(eventType);
    }

    [GeneratedRegex("^[a-z-]+$")]
    private static partial Regex EventTypeRegex();
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public class Tag(string name) : Attribute
{
    public string Name { get; } = name;
}