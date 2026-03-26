namespace Alberto.Dcb.Subscriptions;

public interface ITenantRing
{
    Task<int> RebalanceAsync(IReadOnlyList<string> activeNodeIds, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetAssignedTenantsAsync(string nodeId, CancellationToken ct = default);
    Task RegisterTenantAsync(string tenantId, IReadOnlyList<string> activeNodeIds, CancellationToken ct = default);
}
