using System.Text.Json;

namespace Alberto.Dcb.Testing;

/// <summary>
/// Builds events for appending, so tests do not hand-roll event type resolution and JSON.
/// </summary>
public static class TestEvents
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds an <c>EventToPersist</c> ready to append, resolving its type id from
    /// <c>EventTypeAttribute</c> and serializing the payload to JSON.
    /// </summary>
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
            EventData = JsonSerializer.Serialize(payload, SerializerOptions),
            Tags = tags?.ToArray() ?? [],
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }
}
