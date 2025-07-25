namespace EventStore.Events;

public interface IEventToPersist
{
    Guid Id { get; }
    string EventJson { get; }
    EventType EventType { get; }
    IReadOnlyCollection<EventTag> Tags { get; }
    Dictionary<string, string> Metadata { get; }
    DateTimeOffset Created { get; }
}

public record EventToPersist : IEventToPersist
{
    public Guid Id { get; } = Guid.CreateVersion7();
    public required IReadOnlyCollection<EventTag> Tags { get; init; }
    public required string EventJson { get; init; }
    public required EventType EventType { get; init; }
    public required Dictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public required DateTimeOffset Created { get; init; }
}