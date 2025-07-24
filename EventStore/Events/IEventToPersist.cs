namespace EventStore.Events;

public interface IEventToPersist
{
    Guid Id { get; }
    string EventJson { get; }
    EventType EventType { get; }
    IReadOnlyCollection<DomainId> DomainIds { get; }
    Dictionary<string, string> Metadata { get; }
}