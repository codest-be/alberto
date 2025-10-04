namespace Alberto.EventStore.Serialization;

/// <summary>
/// Interface for deserializing events from JSON to strongly-typed objects
/// </summary>
public interface IEventDeserializer
{
    /// <summary>
    /// Deserializes JSON to a strongly-typed event
    /// </summary>
    /// <typeparam name="TEvent">The target event type</typeparam>
    /// <param name="eventJson">The JSON representation of the event</param>
    /// <returns>The deserialized event instance</returns>
    TEvent Deserialize<TEvent>(string eventJson);

    /// <summary>
    /// Deserializes JSON to the specified event type
    /// </summary>
    /// <param name="eventJson">The JSON representation of the event</param>
    /// <param name="eventType">The target event type</param>
    /// <returns>The deserialized event instance</returns>
    object Deserialize(string eventJson, Type eventType);
}