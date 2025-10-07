using System.Text.Json;
using Alberto.EventStore;
using Alberto.EventStore.Events;

namespace Albert.EventSourcing;

/// <summary>
/// Extension methods for EventStoreFactory to simplify loading and persisting events.
/// </summary>
public static class EventStoreExtensions
{
    /// <summary>
    /// Loads events from the event store based on a StreamQuery.
    /// Returns both the deserialized events and the last event ID for optimistic concurrency.
    /// </summary>
    /// <param name="eventStore">The event store instance</param>
    /// <param name="query">The stream query to filter events</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing the events and the last event ID (or null if no events)</returns>
    public static async Task<(IReadOnlyList<object> Events, Guid? LastEventId)> Load(
        this EventStoreFactory eventStore,
        StreamQuery query,
        CancellationToken cancellationToken = default)
    {
        var envelopes = await eventStore.Stream(query, cancellationToken: cancellationToken);

        var events = envelopes
            .Select(DeserializeEvent)
            .Where(e => e != null)
            .Cast<object>()
            .ToList();

        var lastEventId = envelopes.LastOrDefault()?.Id;

        return (events, lastEventId);
    }

    /// <summary>
    /// Persists events to the event store with optimistic concurrency support.
    /// </summary>
    /// <param name="eventStore">The event store instance</param>
    /// <param name="query">The consistency boundary query</param>
    /// <param name="expectedLastEventId">Expected last event ID for optimistic concurrency</param>
    /// <param name="events">The events to persist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task Persist(
        this EventStoreFactory eventStore,
        StreamQuery query,
        Guid? expectedLastEventId,
        IEnumerable<object> events,
        CancellationToken cancellationToken = default)
    {
        var eventsToPersist = events
            .Select(e => CreateEventToPersist(e, query.Tags))
            .ToList();

        await eventStore.Append(
            eventsToPersist,
            query,
            expectedLastEventId,
            cancellationToken);
    }

    /// <summary>
    /// Persists events for a new aggregate (expectedLastEventId = null).
    /// This is a convenience method for creating new aggregates.
    /// </summary>
    public static Task PersistNew(
        this EventStoreFactory eventStore,
        StreamQuery query,
        IEnumerable<object> events,
        CancellationToken cancellationToken = default)
    {
        return eventStore.Persist(query, null, events, cancellationToken);
    }

    private static object? DeserializeEvent(IEventEnvelope envelope)
    {
        try
        {
            // Try to find the event type from the registry
            var eventTypeName = envelope.EventType.Id;
            var eventType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .FirstOrDefault(t => EventType.GetEventType(t)?.Id == eventTypeName);

            if (eventType == null)
                return null;

            return JsonSerializer.Deserialize(envelope.EventJson, eventType);
        }
        catch
        {
            return null;
        }
    }

    private static IEventToPersist CreateEventToPersist(object @event, IReadOnlyCollection<EventTag> tags)
    {
        var eventType = EventType.GetEventType(@event.GetType());
        if (eventType == null)
            throw new InvalidOperationException(
                $"Event type {@event.GetType().Name} does not have an [EventType] attribute");

        return new EventToPersist
        {
            EventType = eventType,
            EventJson = JsonSerializer.Serialize(@event),
            Tags = tags,
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };
    }
}