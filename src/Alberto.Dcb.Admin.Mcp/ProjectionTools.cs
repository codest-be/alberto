using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Inspection of the projection state table — the read models the control loop maintains.
/// </summary>
[McpServerToolType]
public sealed class ProjectionTools(IAdminReader reader)
{
    [McpServerTool(Name = "alberto_list_projection_types", ReadOnly = true)]
    [Description(
        "Every distinct projection type held in the projection state table. Call this first to " +
        "discover what can be passed to alberto_list_projection_states.")]
    public Task<List<string>> ListProjectionTypesAsync(CancellationToken ct = default) =>
        reader.GetProjectionTypesAsync(ct);

    [McpServerTool(Name = "alberto_list_projection_states", ReadOnly = true)]
    [Description(
        "Stored documents for one projection type, most recently updated first. On a tenant-enabled " +
        "module each tenant has its own rows; a cross-tenant aggregate is stored under the tenant " +
        "id '*'. Use this to check whether a projection actually holds the state you expect.")]
    public Task<List<ProjectionState>> ListProjectionStatesAsync(
        [Description("The projection type, as returned by alberto_list_projection_types.")] string type,
        [Description("Only rows for this tenant. Pass '*' for the cross-tenant aggregate. Omit for all tenants.")] string? tenant = null,
        [Description("Only rows whose document ID contains this substring. Omit for no filtering.")] string? search = null,
        [Description("Maximum rows to return.")] int limit = 50,
        CancellationToken ct = default) =>
        reader.GetProjectionStatesAsync(type, tenant, search, limit, ct);
}
