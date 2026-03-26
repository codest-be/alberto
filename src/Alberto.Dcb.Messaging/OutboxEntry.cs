namespace Alberto.Dcb.Messaging;

/// <summary>
/// The lifecycle status of an outbox entry.
/// </summary>
public enum OutboxEntryStatus
{
    /// <summary>Entry is waiting to be delivered.</summary>
    Pending,

    /// <summary>Entry was successfully delivered to the transport.</summary>
    Delivered,

    /// <summary>Entry failed delivery and is not being retried (until explicitly reset).</summary>
    Failed
}

/// <summary>
/// A single record in the transactional outbox.
/// </summary>
public record OutboxEntry(
    Guid Id,
    Guid SourceEventId,
    string MessageType,
    string Version,
    string Payload,
    Dictionary<string, string> Metadata,
    OutboxEntryStatus Status,
    int RetryCount,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);
