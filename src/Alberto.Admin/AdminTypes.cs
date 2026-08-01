namespace Alberto.Admin;

// ---------------------------------------------------------------------------
// Admin-scope result records (read-only projections of DB rows).
// Factored out of Alberto.Postgres so consumers can depend on admin
// abstractions without pulling in Npgsql.
// ---------------------------------------------------------------------------

/// <summary>
/// Summary of a processor's checkpoint position as seen from the admin surface.
/// </summary>
public sealed record ProcessorInfo(string ProcessorId, long LastPosition, DateTimeOffset? UpdatedAt);

/// <summary>
/// Checkpoint row for a single processor.
/// </summary>
public sealed record CheckpointInfo(string ProcessorId, long LastPosition, DateTimeOffset? UpdatedAt);

/// <summary>
/// Dead letter entry summary returned by admin inspection queries.
/// </summary>
public sealed record DeadLetterInfo(
    Guid Id,
    string ProcessorId,
    string? EventType,
    long? GlobalPosition,
    string? ErrorMessage,
    DateTimeOffset? FailedAt,
    string? TenantId);

/// <summary>
/// Event summary returned by admin inspection queries.
/// </summary>
public sealed record EventInfo(
    long GlobalPosition,
    string EventType,
    string? Tags,
    DateTimeOffset? CreatedAt,
    string? TenantId);

/// <summary>
/// Aggregate system stats.
/// </summary>
public sealed record SystemInfo(
    long? GlobalPosition,
    long ProcessorCount,
    long DeadLetterCount,
    DateTimeOffset? LastEventAt);

/// <summary>
/// Projection state row.
/// </summary>
public sealed record ProjectionState(
    string DocumentId,
    string? TenantId,
    DateTimeOffset? UpdatedAt);

/// <summary>The tenancy shape of the migrated PostgreSQL store.</summary>
public enum AdminTenancyMode
{
    /// <summary>The schema contains no tenant columns or tenant lease table.</summary>
    SingleTenant,

    /// <summary>The schema stores tenant identity and tenant leases.</summary>
    MultiTenant,
}

/// <summary>Topology facts that admin inspection absorbs on behalf of callers.</summary>
public sealed record AdminStoreTopology(AdminTenancyMode TenancyMode)
{
    /// <summary>Whether tenant-aware filters and result fields are available.</summary>
    public bool IsMultiTenant => TenancyMode is AdminTenancyMode.MultiTenant;
}

/// <summary>
/// Admin view of a tenant lease row from the multi-tenant lease table.
/// Distinct from <c>Alberto.Subscriptions.TenantLease</c> (the domain record) —
/// this carries the full admin surface including consumer and replica IDs.
/// </summary>
public sealed record AdminTenantLease(
    string TenantId,
    string ConsumerId,
    string? ReplicaId,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Tenant lease inventory together with the store topology that gives an empty list meaning.
/// </summary>
public sealed record TenantLeaseInventory(
    AdminTenancyMode TenancyMode,
    IReadOnlyList<AdminTenantLease> Leases);

/// <summary>
/// An active processor lease found via admin inspection.
/// </summary>
public sealed record ActiveProcessorLease(string ConsumerId, string? ReplicaId, DateTimeOffset ExpiresAt);

/// <summary>The outcome of an atomic checkpoint rename.</summary>
public enum CheckpointRenameStatus
{
    /// <summary>The source checkpoint was moved to the destination ID.</summary>
    Renamed,

    /// <summary>No checkpoint exists under the source ID.</summary>
    SourceNotFound,

    /// <summary>The destination ID already owns a checkpoint and was not overwritten.</summary>
    DestinationExists,

    /// <summary>The source and destination IDs are identical.</summary>
    SameProcessorId,
}

/// <summary>Result of an atomic checkpoint rename.</summary>
public sealed record CheckpointRenameResult(
    CheckpointRenameStatus Status,
    long? Position = null);

/// <summary>
/// Rebuild state for a single projection as seen from the admin surface.
/// <para>
/// <see cref="Status"/> is one of the lowercase strings <c>idle</c>, <c>rebuilding</c>,
/// <c>ready</c>, <c>completed</c>, or <c>aborted</c>.
/// <see cref="RequestedAction"/> is one of <c>promote</c>, <c>force-promote</c>, <c>abort</c>,
/// or <see langword="null"/> when no operator action is pending.
/// </para>
/// </summary>
public sealed record RebuildStateInfo(
    string ProcessorId,
    string ProjectionType,
    int ActiveVersion,
    int? RebuildingVersion,
    string Status,
    string? RequestedAction,
    long? ReplayedPosition,
    long? TargetPosition,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
