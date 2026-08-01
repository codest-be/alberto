namespace Alberto.Subscriptions;

/// <summary>
/// Context passed to reactor handlers with envelope metadata.
/// </summary>
public sealed class ReactorContext
{
    /// <summary>
    /// Unique identifier for the event.
    /// </summary>
    public required Guid EventId { get; init; }

    /// <summary>
    /// The global position in the event store.
    /// </summary>
    public required long Position { get; init; }

    /// <summary>
    /// When the event was created/persisted.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The tenant this event belongs to. Null in single-tenant mode.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Additional metadata from the event.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    /// <summary>
    /// Create a ReactorContext from an event envelope.
    /// </summary>
    public static ReactorContext FromEnvelope(IEventEnvelope envelope) => new()
    {
        EventId = envelope.Id,
        Position = envelope.GlobalPosition,
        Timestamp = envelope.CreatedAt,
        TenantId = envelope.TenantId,
        Metadata = envelope.Metadata
    };
}
