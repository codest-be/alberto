namespace EventStore.Events;

public record EventEnvelope : IEventEnvelope
{
    public required Guid Id { get; init; }
    public required string EventJson { get; init; }
    public required EventType EventType { get; init; }
    public required Dictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public required DateTimeOffset Created { get; init; }
}