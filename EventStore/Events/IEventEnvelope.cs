namespace EventStore.Events;

public interface IEventEnvelope
{
    Guid Id { get; }
    string EventJson { get; }
    EventType EventType { get; }
    Dictionary<string, string> Metadata { get; }
    DateTimeOffset Created { get; }
}

public interface IEventEnvelope<out TEvent> : IEventEnvelope where TEvent : ISourcedEvent
{
    TEvent Event { get; }
}

public record EventEnvelope : IEventEnvelope
{
    public required Guid Id { get; init; }
    public required string EventJson { get; init; }
    public required EventType EventType { get; init; }
    public required Dictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public required DateTimeOffset Created { get; init; }
}