namespace Alberto;

/// <summary>
/// Represents an event that is ready to be persisted to the event store.
/// This is the input format before the event receives a global position.
/// </summary>
public interface IEventToPersist
{
    /// <summary>
    /// Unique identifier for the event (UUIDv7 recommended for ordering).
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// The event type identifier (e.g., "order-placed").
    /// </summary>
    EventType EventType { get; }

    /// <summary>
    /// Tags for querying and DCB consistency boundaries.
    /// </summary>
    IReadOnlyCollection<EventTag> Tags { get; }

    /// <summary>
    /// The serialized event data as JSON.
    /// </summary>
    /// <remarks>
    /// Must be well-formed JSON and must not contain a NUL character (U+0000) in any string —
    /// neither can be stored in the <c>jsonb</c> column the PostgreSQL backend uses. What is read
    /// back is semantically equal JSON rather than this exact string; see
    /// <see cref="IEventEnvelope.EventData"/>.
    /// </remarks>
    string EventData { get; }

    /// <summary>
    /// Additional metadata (e.g., correlation ID, causation ID, user ID).
    /// </summary>
    IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>
/// Default implementation of <see cref="IEventToPersist"/>.
/// </summary>
public sealed record EventToPersist : IEventToPersist
{
    /// <summary>
    /// Unique identifier for the event. Defaults to a new UUIDv7.
    /// </summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// The event type identifier.
    /// </summary>
    public required EventType EventType { get; init; }

    /// <summary>
    /// Tags for querying and DCB consistency boundaries.
    /// </summary>
    public required IReadOnlyCollection<EventTag> Tags { get; init; }

    /// <summary>
    /// The serialized event data as JSON.
    /// </summary>
    public required string EventData { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
