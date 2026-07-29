using HotChocolate;
using HotChocolate.Types;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// GraphQL queries for the Alberto admin surface.
/// All queries are read-only and delegate to <see cref="IAdminReader"/>.
/// </summary>
public static class AdminQueries
{
    /// <summary>Returns aggregate system stats: global position, processor count, DLQ count.</summary>
    [Query]
    [GraphQLDescription("Aggregate system stats: global position, processor count, DLQ count, last event timestamp.")]
    public static Task<SystemInfo> GetAdminSystemInfo(
        [Service] IAdminReader reader,
        CancellationToken ct) =>
        reader.GetSystemInfoAsync(ct);

    /// <summary>Lists all processor checkpoints ordered by processor ID.</summary>
    [Query]
    [GraphQLDescription("All processor checkpoints with position and last-updated timestamp.")]
    public static Task<List<CheckpointInfo>> GetAdminCheckpoints(
        [Service] IAdminReader reader,
        CancellationToken ct) =>
        reader.GetCheckpointsAsync(ct);

    /// <summary>Gets a single processor's checkpoint.</summary>
    [Query]
    [GraphQLDescription("Checkpoint for a single processor. Returns null if no checkpoint exists.")]
    public static Task<CheckpointInfo?> GetAdminCheckpoint(
        [Service] IAdminReader reader,
        string processorId,
        CancellationToken ct) =>
        reader.GetSingleCheckpointAsync(processorId, ct);

    /// <summary>Lists dead letter entries with optional filters.</summary>
    [Query]
    [GraphQLDescription("Dead letter entries filtered by processor and/or event type. Most recent first.")]
    public static Task<List<DeadLetterInfo>> GetAdminDeadLetters(
        [Service] IAdminReader reader,
        string? processorId,
        string? eventType,
        int limit = 50,
        CancellationToken ct = default) =>
        reader.GetDeadLettersAsync(processorId, eventType, limit, ct);

    /// <summary>Returns total dead letter count across all processors.</summary>
    [Query]
    [GraphQLDescription("Total count of dead letter entries across all processors.")]
    public static Task<int> GetAdminDeadLetterCount(
        [Service] IAdminReader reader,
        CancellationToken ct) =>
        reader.CountAllDeadLettersAsync(ct);

    /// <summary>Lists events from the event log with optional filters.</summary>
    [Query]
    [GraphQLDescription("Event log entries filtered by type and/or tag. Returns events after a given position.")]
    public static Task<List<EventInfo>> GetAdminEvents(
        [Service] IAdminReader reader,
        string? eventType,
        string? tag,
        long afterPosition = 0,
        int limit = 50,
        CancellationToken ct = default) =>
        reader.GetEventsAsync(eventType, tag, afterPosition, limit, ct);

    /// <summary>Lists all processor checkpoints as processor summaries.</summary>
    [Query]
    [GraphQLDescription("All processors with their checkpoint positions.")]
    public static Task<List<ProcessorInfo>> GetAdminProcessors(
        [Service] IAdminReader reader,
        CancellationToken ct) =>
        reader.GetProcessorsAsync(ct);

    /// <summary>Lists distinct projection types.</summary>
    [Query]
    [GraphQLDescription("All distinct projection types in the state table.")]
    public static Task<List<string>> GetAdminProjectionTypes(
        [Service] IAdminReader reader,
        CancellationToken ct) =>
        reader.GetProjectionTypesAsync(ct);

    /// <summary>Lists projection state rows for a given type.</summary>
    [Query]
    [GraphQLDescription("Projection state rows filtered by type, tenant, and search term.")]
    public static Task<List<ProjectionState>> GetAdminProjectionStates(
        [Service] IAdminReader reader,
        string projectionType,
        string? tenant,
        string? search,
        int limit = 50,
        CancellationToken ct = default) =>
        reader.GetProjectionStatesAsync(projectionType, tenant, search, limit, ct);

    /// <summary>Returns tenant lease inventory with topology context.</summary>
    [Query]
    [GraphQLDescription("Current tenant leases with store topology (single/multi-tenant).")]
    public static Task<TenantLeaseInventory> GetAdminTenantLeases(
        [Service] IAdminReader reader,
        CancellationToken ct) =>
        reader.GetTenantLeaseInventoryAsync(ct);

    /// <summary>Returns active processor leases for a given processor.</summary>
    [Query]
    [GraphQLDescription("Active (non-expired) processor leases held for a specific processor.")]
    public static Task<List<ActiveProcessorLease>> GetAdminProcessorLeases(
        [Service] IAdminReader reader,
        string processorId,
        CancellationToken ct) =>
        reader.GetActiveProcessorLeasesAsync(processorId, ct);

    /// <summary>Returns the current global position (head of the event log).</summary>
    [Query]
    [GraphQLDescription("Current maximum global position from the event log. Null if no events exist.")]
    public static Task<long?> GetAdminGlobalPosition(
        [Service] IAdminReader reader,
        CancellationToken ct) =>
        reader.GetGlobalPositionAsync(ct);
}
