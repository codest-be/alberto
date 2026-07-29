using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Store-wide inspection tools: aggregate stats, log head, and tenancy topology.
/// </summary>
[McpServerToolType]
public sealed class SystemTools(IAdminReader reader)
{
    [McpServerTool(Name = "alberto_system_info", ReadOnly = true)]
    [Description(
        "Aggregate health snapshot of the event store: current global log position, number of " +
        "registered processors, total dead-letter count across all processors, and the timestamp " +
        "of the most recent event. Start here when asked how the store is doing.")]
    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default) =>
        reader.GetSystemInfoAsync(ct);

    [McpServerTool(Name = "alberto_global_position", ReadOnly = true)]
    [Description(
        "The current head of the event log — the highest global position appended so far. " +
        "Returns null when the store holds no events. Positions are per-database: on a sharded " +
        "module a position from one shard is meaningless against another.")]
    public Task<long?> GetGlobalPositionAsync(CancellationToken ct = default) =>
        reader.GetGlobalPositionAsync(ct);

    [McpServerTool(Name = "alberto_topology", ReadOnly = true)]
    [Description(
        "Whether this store is single-tenant or multi-tenant. The mode is fixed at migration time " +
        "and cannot be changed once the store holds data, so this also tells you which operations " +
        "are meaningful (tenant lease tools only apply to multi-tenant stores).")]
    public Task<AdminStoreTopology> GetTopologyAsync(CancellationToken ct = default) =>
        reader.GetTopologyAsync(ct);
}
