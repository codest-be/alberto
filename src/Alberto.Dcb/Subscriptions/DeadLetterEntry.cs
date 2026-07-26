namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Represents an event that failed processing and was moved to dead letter storage.
/// </summary>
public sealed record DeadLetterEntry
{
    /// <summary>Unique identifier for this dead letter entry.</summary>
    public required Guid Id { get; init; }

    /// <summary>The processor that failed to handle the event.</summary>
    public required string ProcessorId { get; init; }

    /// <summary>The original event's identifier.</summary>
    public required Guid EventId { get; init; }

    /// <summary>The type of the failed event.</summary>
    public required string EventType { get; init; }

    /// <summary>The serialized event data.</summary>
    public required string EventData { get; init; }

    /// <summary>The error message from the last failure.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>The stack trace from the last failure.</summary>
    public required string? StackTrace { get; init; }

    /// <summary>Total number of processing attempts.</summary>
    public required int AttemptCount { get; init; }

    /// <summary>When the event was moved to dead letter.</summary>
    public required DateTimeOffset FailedAt { get; init; }

    /// <summary>Position of the original event in the global log (0 if unknown).</summary>
    public long GlobalPosition { get; init; } = 0;

    /// <summary>Whether this entry is marked for retry via the CLI.</summary>
    public bool RetryRequested { get; init; } = false;

    /// <summary>
    /// The tenant ID from the original event (fetched from events table during retry).
    /// Null in single-tenant or if not retrieved.
    /// </summary>
    public string? TenantId { get; init; } = null;

    /// <summary>
    /// Event tags from the original event (fetched from events table during retry).
    /// Empty if not retrieved.
    /// </summary>
    public IReadOnlyCollection<string>? Tags { get; init; } = null;

    /// <summary>
    /// Event metadata from the original event (fetched from events table during retry).
    /// Empty if not retrieved.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; } = null;

    /// <summary>
    /// Original event creation timestamp (fetched from events table during retry).
    /// Defaults to FailedAt if not retrieved.
    /// </summary>
    public DateTime? CreatedAt { get; init; } = null;

    /// <summary>
    /// When a retry loop most recently claimed this entry for dispatch,
    /// or null if never claimed / already released.
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; init; } = null;

    /// <summary>
    /// When the active claim lease expires; if in the past the entry is eligible for re-claim
    /// by another worker.
    /// </summary>
    public DateTimeOffset? ClaimExpiresAt { get; init; } = null;

    /// <summary>
    /// Identifier of the worker that holds the active claim (e.g. replica id), for diagnostics.
    /// Null when not claimed.
    /// </summary>
    public string? ClaimedBy { get; init; } = null;

    /// <summary>Opaque fencing token for the active retry claim. Null when not claimed.</summary>
    public Guid? ClaimId { get; init; } = null;
}

/// <summary>
/// A time-bounded, token-fenced claim on a dead-letter entry.
/// The claim must be presented unchanged when completing or abandoning the retry so a worker
/// whose lease expired cannot mutate a newer worker's claim.
/// </summary>
public sealed record DeadLetterClaim(
    DeadLetterEntry Entry,
    Guid Token,
    DateTimeOffset ExpiresAt);
