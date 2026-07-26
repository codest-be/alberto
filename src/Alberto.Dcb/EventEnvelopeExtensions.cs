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
    /// <remarks>
    /// This method performs raw JSON deserialization and bypasses any registered upcaster chains.
    /// If you have upcasters registered, they will not fire when you call this method.
    /// Prefer injecting <see cref="EventSerializer"/> and calling
    /// <c>serializer.Deserialize(envelope)</c> then casting to the expected type.
    /// </remarks>
    /// <typeparam name="T">Target type. Must match the event's actual shape.</typeparam>
    /// <param name="envelope">The event envelope whose data is deserialized.</param>
    /// <returns>The deserialized event.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the JSON payload cannot be deserialized to <typeparamref name="T"/>.
    /// </exception>
    [Obsolete(
        "ParseEvent<T>() bypasses upcasters — registered upcaster chains never fire on this path. " +
        "Inject EventSerializer and call serializer.Deserialize(envelope) instead, then cast to the " +
        "expected event type, or use EventEnvelopeExtensions.DeserializeEvent<T>. " +
        "This method will be removed in a future release.")]
    public static T ParseEvent<T>(this IEventEnvelope envelope)
        => JsonSerializer.Deserialize<T>(envelope.EventData)
           ?? throw new InvalidOperationException(
               $"Failed to deserialize event '{envelope.EventType.Id}' to type '{typeof(T).Name}'");

    /// <summary>
    /// The single internal seam for <see cref="IEventEnvelope"/> → CLR event object conversion.
    /// When <paramref name="serializer"/> is provided, routes through
    /// <see cref="EventSerializer.Deserialize"/> (and its upcaster chain) and verifies
    /// the result is the expected type. When null, falls back to raw JSON deserialization
    /// for standalone / backward-compatible usage.
    /// </summary>
    /// <remarks>
    /// All runtime consumer surfaces (<c>DeclaredAsyncProjection</c>, <c>FunctionalReactor</c>,
    /// <c>SyncReactor</c>, <c>MessageMappingRegistryExtensions</c>) call this method rather than
    /// <c>JsonSerializer.Deserialize&lt;T&gt;(string)</c> directly, so the upcaster chain always
    /// fires when an <see cref="EventSerializer"/> has been configured for the module.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the serializer produces a type that is not assignable to
    /// <typeparamref name="TEvent"/>, or when raw JSON deserialization yields null.
    /// </exception>
    internal static TEvent DeserializeEvent<TEvent>(IEventEnvelope envelope, EventSerializer? serializer)
        where TEvent : IEvent
    {
        if (serializer is not null)
        {
            var deserialized = serializer.Deserialize(envelope);
            if (deserialized is not TEvent typed)
                throw new InvalidOperationException(
                    $"EventSerializer produced '{deserialized.GetType().Name}' for event " +
                    $"'{envelope.EventType.Id}' but the handler expects '{typeof(TEvent).Name}'. " +
                    "Ensure the upcaster chain terminates at the type declared for this handler.");
            return typed;
        }

        // EV-1 guard: if the stored event version is older than the version the CLR handler
        // type declares, raw JSON deserialization would silently produce a stale shape — partial
        // or default-filled fields instead of the correctly upcasted object.  Fail loud so the
        // caller discovers the missing serializer rather than writing wrong data.
        var declaredVersion = EventTypeAttribute.GetEventType(typeof(TEvent))?.Version ?? 1;
        if (envelope.EventType.Version < declaredVersion)
            throw new InvalidOperationException(
                $"Event '{envelope.EventType.Id}' is stored at schema version " +
                $"{envelope.EventType.Version} but the handler type '{typeof(TEvent).Name}' " +
                $"expects version {declaredVersion}. Raw JSON deserialization would produce " +
                "stale state. Supply an EventSerializer so the upcaster chain runs: " +
                "inject EventSerializer and call serializer.Deserialize(envelope), " +
                "or pass the serializer through the consumer's constructor.");

        return JsonSerializer.Deserialize<TEvent>(envelope.EventData)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize event '{envelope.EventType.Id}' to type '{typeof(TEvent).Name}'.");
    }
}
