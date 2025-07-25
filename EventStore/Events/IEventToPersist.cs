namespace EventStore.Events;

public interface IEventToPersist
{
    Guid Id { get; }
    string EventJson { get; }
    EventType EventType { get; }
    IReadOnlyCollection<EventTag> Tags { get; }
    Dictionary<string, string> Metadata { get; }
}