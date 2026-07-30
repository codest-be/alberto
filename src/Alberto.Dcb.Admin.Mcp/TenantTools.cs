using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Tenant and processor lease inspection, plus the operator action that forces reacquisition.
/// Meaningful only on multi-tenant stores — check <c>alberto_topology</c> first.
/// </summary>
[McpServerToolType]
public sealed class TenantTools(IAdminReader reader, IAdminOperator op)
{
    [McpServerTool(Name = "alberto_list_tenant_leases", ReadOnly = true)]
    [Description(
        "Current tenant lease assignments: which consumer holds each tenant's lease and when it " +
        "expires. Returns an empty inventory on a single-tenant store. Use this to see how tenant " +
        "work is distributed across running instances.")]
    public Task<TenantLeaseInventory> ListTenantLeasesAsync(CancellationToken ct = default) =>
        reader.GetTenantLeaseInventoryAsync(ct);

    [McpServerTool(Name = "alberto_list_tenants", ReadOnly = true)]
    [Description(
        "Every tenant the store has seen an event for, ordered by ID. Empty on a single-tenant " +
        "store. Prefer this over alberto_list_tenant_leases when you need the tenants that exist: " +
        "a lease is held only while a consumer is actively processing, so an idle tenant has none.")]
    public Task<List<string>> ListTenantsAsync(CancellationToken ct = default) =>
        reader.GetTenantsAsync(ct);

    [McpServerTool(Name = "alberto_list_processor_leases", ReadOnly = true)]
    [Description(
        "The active, non-expired leases held for one processor. A checkpoint mutation on a " +
        "processor that still holds leases can be fenced or overwritten by the live holder, so " +
        "check this before calling alberto_set_checkpoint or alberto_reset_checkpoint.")]
    public Task<List<ActiveProcessorLease>> ListProcessorLeasesAsync(
        [Description("The processor whose leases are listed.")] string processorId,
        CancellationToken ct = default) =>
        reader.GetActiveProcessorLeasesAsync(processorId, ct);

    [McpServerTool(Name = "alberto_release_tenant_leases", Destructive = true, Idempotent = true)]
    [Description(
        "Releases tenant leases so running instances must reacquire them, which redistributes " +
        "tenant work across the current set of instances. Safe but disruptive: in-flight tenant " +
        "processing stops and restarts. Returns the number of leases released.")]
    public Task<int> ReleaseTenantLeasesAsync(
        [Description("Only release leases held by this consumer group. Omit to release every tenant lease.")] string? consumerId = null,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default) =>
        op.ReleaseTenantLeasesAsync(consumerId, operatorId, ct);
}
