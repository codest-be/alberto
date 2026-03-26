namespace Alberto.Dcb;

/// <summary>
/// Represents a persisted event with its assigned global position.
/// This is the output format after an event has been stored.
/// </summary>
public interface IEventEnvelope
{
    /// <summary>
    /// Unique identifier for the event.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// The tenant this event belongs to. Null in single-tenant mode.
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// The global position in the event store (monotonically increasing).
    /// </summary>
    long GlobalPosition { get; }

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
    string EventData { get; }

    /// <summary>
    /// Additional metadata (e.g., correlation ID, causation ID, user ID).
    /// </summary>
    IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Timestamp when the event was created/persisted (always UTC).
    /// </summary>
    DateTime CreatedAt { get; }
}

/// <summary>
/// Generic event envelope that includes the deserialized event.
/// </summary>
/// <typeparam name="TEvent">The type of the domain event.</typeparam>
public interface IEventEnvelope<out TEvent> : IEventEnvelope where TEvent : IEvent
{
    /// <summary>
    /// The deserialized domain event.
    /// </summary>
    TEvent Event { get; }
}

/// <summary>
/// Default implementation of <see cref="IEventEnvelope"/>.
/// </summary>
public sealed record EventEnvelope : IEventEnvelope
{
    /// <summary>
    /// Unique identifier for the event.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// The tenant this event belongs to. Null in single-tenant mode.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The global position in the event store.
    /// </summary>
    public required long GlobalPosition { get; init; }

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
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    /// <summary>
    /// Timestamp when the event was created/persisted (always UTC).
    /// </summary>
    public required DateTime CreatedAt { get; init; }
}
