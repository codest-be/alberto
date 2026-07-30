using HotChocolate;
using HotChocolate.Types;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// GraphQL queries for the Alberto admin surface.
/// All queries are read-only and delegate to the <see cref="IAdminReader"/> for the
/// module named by the <c>module</c> argument, or the default module when it is omitted.
/// </summary>
/// <remarks>
/// A host can run several Alberto modules in one process — the Orders example runs
/// <c>orders</c> and <c>payments</c>. Each is a separate event store, so every query
/// answers for exactly one of them. There is deliberately no query that spans modules:
/// two stores' positions are unrelated counters, and a merged figure would be a number
/// that describes nothing.
/// </remarks>
public static class AdminQueries
{
    /// <summary>Lists the event stores this admin surface can address.</summary>
    [Query]
    [GraphQLDescription("Event stores this admin surface can address. Pass a key as the 'module' argument on any other admin field. Positions from different modules are unrelated and must not be compared.")]
    public static async Task<List<AdminModuleInfo>> GetAdminModules(
        [Service] IAdminModuleRegistry modules,
        CancellationToken ct)
    {
        var result = new List<AdminModuleInfo>(modules.Modules.Count);
        foreach (var descriptor in modules.Modules)
        {
            var topology = await modules.GetReader(descriptor.Key).GetTopologyAsync(ct);
            result.Add(new AdminModuleInfo(
                descriptor.Key, descriptor.Schema, descriptor.IsDefault, topology.TenancyMode));
        }

        return result;
    }

    /// <summary>Returns aggregate system stats: global position, processor count, DLQ count.</summary>
    [Query]
    [GraphQLDescription("Aggregate system stats: global position, processor count, DLQ count, last event timestamp.")]
    public static Task<SystemInfo> GetAdminSystemInfo(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetSystemInfoAsync(ct);

    /// <summary>Lists all processor checkpoints ordered by processor ID.</summary>
    [Query]
    [GraphQLDescription("All processor checkpoints with position and last-updated timestamp.")]
    public static Task<List<CheckpointInfo>> GetAdminCheckpoints(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetCheckpointsAsync(ct);

    /// <summary>Gets a single processor's checkpoint.</summary>
    [Query]
    [GraphQLDescription("Checkpoint for a single processor. Returns null if no checkpoint exists.")]
    public static Task<CheckpointInfo?> GetAdminCheckpoint(
        [Service] IAdminModuleRegistry modules,
        string processorId,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetSingleCheckpointAsync(processorId, ct);

    /// <summary>Lists dead letter entries with optional filters.</summary>
    [Query]
    [GraphQLDescription("Dead letter entries filtered by processor, event type, and/or tenant. Most recent first. Filtering by tenant on a single-tenant module is an error.")]
    public static Task<List<DeadLetterInfo>> GetAdminDeadLetters(
        [Service] IAdminModuleRegistry modules,
        string? processorId,
        string? eventType,
        string? tenant,
        string? module,
        int limit = 50,
        CancellationToken ct = default) =>
        modules.GetReader(module).GetDeadLettersAsync(processorId, eventType, tenant, limit, ct);

    /// <summary>Returns total dead letter count across all processors.</summary>
    [Query]
    [GraphQLDescription("Total count of dead letter entries across all processors.")]
    public static Task<int> GetAdminDeadLetterCount(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).CountAllDeadLettersAsync(ct);

    /// <summary>Lists events from the event log with optional filters.</summary>
    [Query]
    [GraphQLDescription("Event log entries filtered by type, tag, and/or tenant. Returns events after a given position. Filtering by tenant on a single-tenant module is an error.")]
    public static Task<List<EventInfo>> GetAdminEvents(
        [Service] IAdminModuleRegistry modules,
        string? eventType,
        string? tag,
        string? tenant,
        string? module,
        long afterPosition = 0,
        int limit = 50,
        CancellationToken ct = default) =>
        modules.GetReader(module).GetEventsAsync(eventType, tag, tenant, afterPosition, limit, ct);

    /// <summary>Lists all processor checkpoints as processor summaries.</summary>
    [Query]
    [GraphQLDescription("All processors with their checkpoint positions.")]
    public static Task<List<ProcessorInfo>> GetAdminProcessors(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetProcessorsAsync(ct);

    /// <summary>Lists distinct projection types.</summary>
    [Query]
    [GraphQLDescription("All distinct projection types in the state table.")]
    public static Task<List<string>> GetAdminProjectionTypes(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetProjectionTypesAsync(ct);

    /// <summary>Lists projection state rows for a given type.</summary>
    [Query]
    [GraphQLDescription("Projection state rows filtered by type, tenant, and search term.")]
    public static Task<List<ProjectionState>> GetAdminProjectionStates(
        [Service] IAdminModuleRegistry modules,
        string projectionType,
        string? tenant,
        string? search,
        string? module,
        int limit = 50,
        CancellationToken ct = default) =>
        modules.GetReader(module).GetProjectionStatesAsync(projectionType, tenant, search, limit, ct);

    /// <summary>Returns tenant lease inventory with topology context.</summary>
    [Query]
    [GraphQLDescription("Current tenant leases with store topology (single/multi-tenant).")]
    public static Task<TenantLeaseInventory> GetAdminTenantLeases(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetTenantLeaseInventoryAsync(ct);

    /// <summary>Returns every tenant the module's store has seen an event for.</summary>
    [Query]
    [GraphQLDescription(
        "Every tenant this module's store has seen an event for, ordered by ID. Empty on a "
        + "single-tenant store. Unlike adminTenantLeases this does not depend on a consumer "
        + "currently holding a lease, so it is the list to offer as a tenant filter.")]
    public static Task<List<string>> GetAdminTenants(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetTenantsAsync(ct);

    /// <summary>Returns active processor leases for a given processor.</summary>
    [Query]
    [GraphQLDescription("Active (non-expired) processor leases held for a specific processor.")]
    public static Task<List<ActiveProcessorLease>> GetAdminProcessorLeases(
        [Service] IAdminModuleRegistry modules,
        string processorId,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetActiveProcessorLeasesAsync(processorId, ct);

    /// <summary>Returns the current global position (head of the event log).</summary>
    [Query]
    [GraphQLDescription("Current maximum global position from this module's event log. Null if no events exist. Not comparable with another module's position.")]
    public static Task<long?> GetAdminGlobalPosition(
        [Service] IAdminModuleRegistry modules,
        string? module,
        CancellationToken ct) =>
        modules.GetReader(module).GetGlobalPositionAsync(ct);

    /// <summary>Lists rebuild state for all projections, or for a single processor.</summary>
    [Query]
    [GraphQLDescription("Rebuild state for all projections, or for a single processor when processorId is supplied. Returns an empty list when no rebuild has ever been started.")]
    public static Task<List<RebuildStateInfo>> GetAdminRebuildStates(
        [Service] IAdminModuleRegistry modules,
        string? processorId,
        string? module,
        CancellationToken ct = default) =>
        modules.GetReader(module).GetRebuildStatesAsync(processorId, ct);
}

/// <summary>One addressable event store, as seen by the admin surface.</summary>
/// <param name="Key">Pass this as the <c>module</c> argument on any admin field.</param>
/// <param name="Schema">The database schema holding the module's tables.</param>
/// <param name="IsDefault">Whether this module answers when <c>module</c> is omitted.</param>
/// <param name="TenancyMode">
/// Whether the module's schema carries tenant identity. A client should only offer a
/// tenant filter when this is <see cref="AdminTenancyMode.MultiTenant"/>.
/// </param>
public sealed record AdminModuleInfo(
    string Key,
    string Schema,
    bool IsDefault,
    AdminTenancyMode TenancyMode);
