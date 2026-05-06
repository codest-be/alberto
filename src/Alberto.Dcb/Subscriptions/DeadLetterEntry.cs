namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Represents an event that failed processing and was moved to dead letter storage.
/// </summary>
/// <param name="Id">Unique identifier for this dead letter entry.</param>
/// <param name="ProcessorId">The processor that failed to handle the event.</param>
/// <param name="EventId">The original event's identifier.</param>
/// <param name="EventType">The type of the failed event.</param>
/// <param name="EventData">The serialized event data.</param>
/// <param name="ErrorMessage">The error message from the last failure.</param>
/// <param name="StackTrace">The stack trace from the last failure.</param>
/// <param name="AttemptCount">Total number of processing attempts.</param>
/// <param name="FailedAt">When the event was moved to dead letter.</param>
/// <param name="GlobalPosition">Position of the original event in the global log (0 if unknown).</param>
/// <param name="RetryRequested">Whether this entry is marked for retry via the CLI.</param>
/// <param name="TenantId">The tenant ID from the original event (fetched from events table during retry). Null in single-tenant or if not retrieved.</param>
/// <param name="Tags">Event tags from the original event (fetched from events table during retry). Empty if not retrieved.</param>
/// <param name="Metadata">Event metadata from the original event (fetched from events table during retry). Empty if not retrieved.</param>
/// <param name="CreatedAt">Original event creation timestamp (fetched from events table during retry). Defaults to FailedAt if not retrieved.</param>
/// <param name="ClaimedAt">When a retry loop most recently claimed this entry for dispatch, or null if never claimed / already released.</param>
/// <param name="ClaimExpiresAt">When the active claim lease expires; if in the past the entry is eligible for re-claim by another worker.</param>
/// <param name="ClaimedBy">Identifier of the worker that holds the active claim (e.g. replica id), for diagnostics. Null when not claimed.</param>
public sealed record DeadLetterEntry(
    Guid Id,
    string ProcessorId,
    Guid EventId,
    string EventType,
    string EventData,
    string ErrorMessage,
    string? StackTrace,
    int AttemptCount,
    DateTimeOffset FailedAt,
    long GlobalPosition = 0,
    bool RetryRequested = false,
    string? TenantId = null,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTime? CreatedAt = null,
    DateTimeOffset? ClaimedAt = null,
    DateTimeOffset? ClaimExpiresAt = null,
    string? ClaimedBy = null);
