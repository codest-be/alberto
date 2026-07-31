namespace Alberto.Dcb.Admin;

// ---------------------------------------------------------------------------
// Admin audit events — appended to the DCB event log in the same transaction
// as the state change. Queryable via admin tags (e.g. alberto events --tag admin-rebuild).
// ---------------------------------------------------------------------------

/// <summary>
/// An operator reset a processor checkpoint (deleted it entirely).
/// </summary>
[EventType("admin-checkpoint-reset")]
public sealed record AdminCheckpointReset(
    string ProcessorId,
    string OperatorId) : IEvent;

/// <summary>
/// An operator rewound a processor checkpoint to an earlier position.
/// </summary>
[EventType("admin-checkpoint-rewound")]
public sealed record AdminCheckpointRewound(
    string ProcessorId,
    long FromPosition,
    long ToPosition,
    string OperatorId) : IEvent;

/// <summary>
/// An operator retried dead letters for a processor by rewinding its checkpoint
/// to one position before the earliest dead letter.
/// </summary>
[EventType("admin-dead-letters-retried")]
public sealed record AdminDeadLettersRetried(
    string ProcessorId,
    long RewindPosition,
    int DeletedCount,
    string OperatorId) : IEvent;

/// <summary>
/// An operator marked a processor's dead letters for retry, leaving the rows in place for the
/// retry loop to pick up. Distinct from <see cref="AdminDeadLettersRetried"/>, which records the
/// heavier retry-by-rewind: that one moves the checkpoint and deletes the rows.
/// </summary>
[EventType("admin-dead-letters-marked-for-retry")]
public sealed record AdminDeadLettersMarkedForRetry(
    string ProcessorId,
    int MarkedCount,
    string OperatorId) : IEvent;

/// <summary>
/// An operator cleared dead letters.
/// </summary>
[EventType("admin-dead-letters-cleared")]
public sealed record AdminDeadLettersCleared(
    int DeletedCount,
    string OperatorId) : IEvent;

/// <summary>
/// An operator renamed a checkpoint, carrying a processor's position across a rename of the
/// processor itself.
/// </summary>
[EventType("admin-checkpoint-renamed")]
public sealed record AdminCheckpointRenamed(
    string FromProcessorId,
    string ToProcessorId,
    long Position,
    string OperatorId) : IEvent;

/// <summary>
/// An operator started a zero-downtime projection rebuild.
/// </summary>
[EventType("admin-rebuild-started")]
public sealed record AdminRebuildStarted(
    string ProcessorId,
    string ProjectionType,
    int RebuildingVersion,
    long TargetPosition,
    string OperatorId) : IEvent;

/// <summary>
/// An operator promoted a finished rebuild, making it the active version.
/// </summary>
[EventType("admin-rebuild-promoted")]
public sealed record AdminRebuildPromoted(
    string ProcessorId,
    string Status,
    string OperatorId) : IEvent;

/// <summary>
/// An operator aborted a rebuild in flight.
/// </summary>
[EventType("admin-rebuild-aborted")]
public sealed record AdminRebuildAborted(
    string ProcessorId,
    string Status,
    string OperatorId) : IEvent;

/// <summary>
/// An operator released tenant leases, forcing the application to reacquire them.
/// </summary>
[EventType("admin-tenant-leases-released")]
public sealed record AdminTenantLeasesReleased(
    string? ConsumerId,
    int ReleasedCount,
    string OperatorId) : IEvent;

/// <summary>
/// An operator purged delivered outbox entries.
/// </summary>
/// <remarks>
/// Only delivered entries are ever removed, so this records housekeeping, not lost work — but it
/// is still a deletion, and <see cref="Before"/> is what makes it reconstructible: the count alone
/// cannot say which rows went.
/// </remarks>
[EventType("admin-outbox-purged")]
public sealed record AdminOutboxPurged(
    DateTimeOffset Before,
    int DeletedCount,
    string OperatorId) : IEvent;
