namespace Alberto.Dcb.Messaging;

/// <summary>
/// The lifecycle status of an outbox entry.
/// </summary>
public enum OutboxEntryStatus
{
    /// <summary>Entry is waiting to be delivered.</summary>
    Pending,

    /// <summary>Entry has been claimed by a relay instance and is currently being delivered.</summary>
    Processing,

    /// <summary>Entry was successfully delivered to the transport.</summary>
    Delivered,

    /// <summary>Entry failed delivery and is not being retried (until explicitly reset).</summary>
    Failed
}

/// <summary>
/// A single record in the transactional outbox.
/// <c>Destination</c> and <c>RoutingHint</c> are optional transport fields copied from the
/// originating <see cref="ExternalMessage"/>; both are null when the producer did not supply them.
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
    DateTimeOffset? DeliveredAt,
    string? TenantId = null,
    string? Destination = null,
    string? RoutingHint = null);

/// <summary>
/// A time-bounded, token-fenced claim on an outbox entry.
/// The claim token must be presented when completing delivery so a relay whose lease expired
/// cannot overwrite the result of a newer relay that reclaimed the same entry.
/// </summary>
public sealed record OutboxClaim(
    OutboxEntry Entry,
    Guid Token,
    DateTimeOffset ExpiresAt);
