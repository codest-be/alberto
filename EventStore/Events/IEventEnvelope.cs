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