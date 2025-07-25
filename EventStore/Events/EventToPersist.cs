namespace EventStore.Events;

public record EventToPersist : IEventToPersist
{
    public Guid Id { get; } = Guid.CreateVersion7();
    public required IReadOnlyCollection<EventTag> Tags { get; init; }
    public required string EventJson { get; init; }
    public required EventType EventType { get; init; }
    public required Dictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}