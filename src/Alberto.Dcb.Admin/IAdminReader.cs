namespace Alberto.Dcb.Admin;

/// <summary>
/// Read-only inspection queries for the Alberto event store admin surface.
/// Covers the full event-store schema and cannot be expressed through the per-processor
/// <c>ICheckpointStore</c> / <c>IDeadLetterStore</c> interfaces.
/// </summary>
public interface IAdminReader
{
    /// <summary>
    /// Gets the current maximum global position from the event log.
    /// Returns <see langword="null"/> when no events have been appended.
    /// </summary>
    Task<long?> GetGlobalPositionAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all processor checkpoints ordered by processor ID.
    /// </summary>
    Task<List<ProcessorInfo>> GetProcessorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all processor checkpoints ordered by processor ID.
    /// </summary>
    Task<List<CheckpointInfo>> GetCheckpointsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the checkpoint for a single processor, or <see langword="null"/> if not found.
    /// </summary>
    Task<CheckpointInfo?> GetSingleCheckpointAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Lists dead letter entries with optional filtering by processor and event type.
    /// Returns at most <paramref name="limit"/> results ordered by <c>failed_at DESC</c>.
    /// </summary>
    Task<List<DeadLetterInfo>> GetDeadLettersAsync(
        string? processorId,
        string? type,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Lists events from the event log with optional filtering by type and tag.
    /// Returns at most <paramref name="limit"/> results after <paramref name="afterPosition"/>.
    /// </summary>
    Task<List<EventInfo>> GetEventsAsync(
        string? type,
        string? tag,
        long afterPosition,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns aggregate system stats: current global position, processor count,
    /// total dead letter count, and timestamp of the most recent event.
    /// </summary>
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all distinct projection types present in the projection-state table.
    /// </summary>
    Task<List<string>> GetProjectionTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists projection state rows for the given type with optional tenant and substring filters.
    /// Returns at most <paramref name="limit"/> rows ordered by <c>updated_at DESC</c>.
    /// </summary>
    Task<List<ProjectionState>> GetProjectionStatesAsync(
        string type,
        string? tenant,
        string? search,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the store topology (single-tenant vs multi-tenant).
    /// </summary>
    Task<AdminStoreTopology> GetTopologyAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all current tenant lease rows ordered by tenant ID.
    /// Returns empty list for single-tenant stores.
    /// </summary>
    Task<TenantLeaseInventory> GetTenantLeaseInventoryAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the active (non-expired) processor leases held for the given processor ID.
    /// </summary>
    Task<List<ActiveProcessorLease>> GetActiveProcessorLeasesAsync(
        string processorId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the total count of dead letter entries across all processors.
    /// </summary>
    Task<int> CountAllDeadLettersAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically renames a checkpoint from one processor ID to another.
    /// </summary>
    Task<CheckpointRenameResult> RenameCheckpointAsync(
        string fromProcessorId,
        string toProcessorId,
        CancellationToken ct = default);
}
