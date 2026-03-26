using Alberto.Dcb;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// Delegate that maps a persisted event envelope to an outgoing external message.
/// Return <c>null</c> to suppress publishing for this event.
/// </summary>
public delegate ExternalMessage? EventToMessageMapper(IEventEnvelope envelope);

/// <summary>
/// Registry that maps event types to their corresponding external message shapes.
/// </summary>
public interface IMessageMappingRegistry
{
    /// <summary>Registers a mapper for the given event type string identifier.</summary>
    void Map(string eventType, EventToMessageMapper mapper);

    /// <summary>Registers a mapper using the event type derived from <typeparamref name="TEvent"/>'s <see cref="EventTypeAttribute"/>.</summary>
    void Map<TEvent>(EventToMessageMapper mapper) where TEvent : IEvent;

    /// <summary>Attempts to map an event envelope to an external message. Returns <c>null</c> if no mapping is registered.</summary>
    ExternalMessage? TryMap(IEventEnvelope envelope);

    /// <summary>The set of event type identifiers that have registered mappers.</summary>
    IReadOnlySet<string> MappedEventTypes { get; }
}
