using System.Text.Json;

namespace Alberto.Testing;

/// <summary>
/// Builds events for appending, so tests do not hand-roll event type resolution and JSON.
/// </summary>
public static class TestEvents
{
    /// <summary>
    /// Builds an <c>EventToPersist</c> ready to append, resolving its type id from
    /// <c>EventTypeAttribute</c> and serializing the payload to JSON.
    /// </summary>
    /// <remarks>
    /// Payload is serialized with <see cref="JsonSerializer"/>'s default options so that
    /// <see cref="EventSerializer"/> — which also uses the default options — can round-trip
    /// the payload correctly in projection handlers.
    /// </remarks>
    /// <param name="payload">The event payload.</param>
    /// <param name="tags">Tags to attach. Defaults to none.</param>
    /// <param name="metadata">Metadata to attach. Defaults to none.</param>
    public static EventToPersist NewEvent<TEvent>(
        TEvent payload,
        IEnumerable<EventTag>? tags = null,
        IReadOnlyDictionary<string, string>? metadata = null)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new EventToPersist
        {
            Id = Guid.CreateVersion7(),
            EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(TEvent))),
            EventData = JsonSerializer.Serialize(payload),
            Tags = tags?.ToArray() ?? [],
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }
}
