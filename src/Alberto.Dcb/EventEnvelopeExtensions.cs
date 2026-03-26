using System.Text.Json;

namespace Alberto.Dcb;

/// <summary>
/// Extension methods for <see cref="IEventEnvelope"/>.
/// </summary>
public static class EventEnvelopeExtensions
{
    /// <summary>
    /// Deserializes the event data payload to the requested CLR type.
    /// </summary>
    /// <typeparam name="T">Target type. Must match the event's actual shape.</typeparam>
    /// <param name="envelope">The event envelope whose data is deserialized.</param>
    /// <returns>The deserialized event.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the JSON payload cannot be deserialized to <typeparamref name="T"/>.
    /// </exception>
    public static T ParseEvent<T>(this IEventEnvelope envelope)
        => JsonSerializer.Deserialize<T>(envelope.EventData)
           ?? throw new InvalidOperationException(
               $"Failed to deserialize event '{envelope.EventType.Id}' to type '{typeof(T).Name}'");
}
