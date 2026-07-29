using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Read-only access to the event log itself.
/// </summary>
[McpServerToolType]
public sealed class EventTools(IAdminReader reader)
{
    [McpServerTool(Name = "alberto_list_events", ReadOnly = true)]
    [Description(
        "Reads events from the append-only log. Tags are first-class filters in a DCB store, so " +
        "filtering by tag is the normal way to find every event touching one entity. Page through " +
        "the log by passing the last position you saw as afterPosition. Positions are per-database: " +
        "on a sharded module they are not comparable across shards.")]
    public Task<List<EventInfo>> ListEventsAsync(
        [Description("Only events of this type. Omit for all types.")] string? type = null,
        [Description("Only events carrying this tag value, e.g. 'order:1234'. Omit for all events.")] string? tag = null,
        [Description("Only events belonging to this tenant. Omit for all tenants. Passing a tenant on a single-tenant store is an error.")] string? tenant = null,
        [Description("Return events after this global position. Pass 0 to start from the beginning.")] long afterPosition = 0,
        [Description("Maximum events to return.")] int limit = 20,
        CancellationToken ct = default) =>
        reader.GetEventsAsync(type, tag, tenant, afterPosition, limit, ct);
}
