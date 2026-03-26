using Alberto.Dcb;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// Default in-memory implementation of <see cref="IMessageMappingRegistry"/>.
/// </summary>
public sealed class MessageMappingRegistry : IMessageMappingRegistry
{
    private readonly Dictionary<string, EventToMessageMapper> _mappers = new();

    /// <inheritdoc/>
    public void Map(string eventType, EventToMessageMapper mapper)
        => _mappers[eventType] = mapper;

    /// <inheritdoc/>
    public void Map<TEvent>(EventToMessageMapper mapper) where TEvent : IEvent
        => _mappers[EventTypeAttribute.GetEventTypeId(typeof(TEvent))] = mapper;

    /// <inheritdoc/>
    public ExternalMessage? TryMap(IEventEnvelope envelope)
        => _mappers.TryGetValue(envelope.EventType.Id, out var mapper) ? mapper(envelope) : null;

    /// <inheritdoc/>
    public IReadOnlySet<string> MappedEventTypes => _mappers.Keys.ToHashSet();
}
