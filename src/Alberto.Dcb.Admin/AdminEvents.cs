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
/// An operator cleared dead letters.
/// </summary>
[EventType("admin-dead-letters-cleared")]
public sealed record AdminDeadLettersCleared(
    int DeletedCount,
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
