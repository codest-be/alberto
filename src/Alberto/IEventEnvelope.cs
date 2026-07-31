namespace Alberto;

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
    /// The tags on this event, including the framework-reserved <c>_version:N</c> schema-version tag
    /// appended last, alongside any application tags extracted from <c>[Tag]</c>-annotated properties.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>_version:N</c> tag is stamped automatically on every event by the framework and will always
    /// appear in this collection. It cannot be authored by application code: the
    /// <see cref="EventTag"/> public constructor and <c>[Tag(...)]</c> throw
    /// <see cref="ArgumentException"/> for any concept starting with
    /// <see cref="EventTag.ReservedConceptPrefix"/>, so no boundary can be built over it either.
    /// </para>
    /// <para>
    /// To read the schema version, use <c>envelope.EventType.Version</c> — do not string-parse
    /// the tag. To skip the version tag when iterating, filter by
    /// <c>tag.Concept != EventTag.SchemaVersionConcept</c>.
    /// </para>
    /// </remarks>
    IReadOnlyCollection<EventTag> Tags { get; }

    /// <summary>
    /// The serialized event data as JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is JSON semantically equal to what was appended, not the same string.</strong>
    /// The PostgreSQL backend stores it in a <c>jsonb</c> column, which parses the payload and
    /// re-emits it from the parsed tree: whitespace is dropped, object keys come back sorted,
    /// duplicate keys collapse to the last one, and numbers are re-rendered
    /// (<c>1E+30</c> comes back as thirty zeros). The in-memory backend normalizes too, differently
    /// — deliberately, so that code which depends on the byte form fails in tests rather than in
    /// production.
    /// </para>
    /// <para>
    /// Compare payloads with <c>JsonNode.DeepEquals</c> or by deserializing, never with <c>==</c>.
    /// </para>
    /// </remarks>
    string EventData { get; }

    /// <summary>
    /// Additional metadata (e.g., correlation ID, causation ID, user ID).
    /// </summary>
    IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Timestamp when the event was created/persisted. The offset is always UTC (+00:00).
    /// </summary>
    DateTimeOffset CreatedAt { get; }
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

    /// <inheritdoc cref="IEventEnvelope.Tags"/>
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
    /// Timestamp when the event was created/persisted. The offset is always UTC (+00:00).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
