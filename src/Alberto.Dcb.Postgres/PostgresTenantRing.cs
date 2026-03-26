using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ITenantRing"/>.
/// Persists deterministic tenant-to-node assignments based on consistent hashing,
/// so all replicas agree on which node owns which tenant without coordination.
/// </summary>
public sealed class PostgresTenantRing(NpgsqlDataSource dataSource, string? schema = null) : ITenantRing
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly SchemaQualifier _schema = new(schema);

    /// <inheritdoc/>
    public async Task<int> RebalanceAsync(IReadOnlyList<string> activeNodeIds, CancellationToken ct = default)
    {
        if (activeNodeIds.Count == 0)
            return 0;

        // Read all current assignments
        var assignments = new List<(string tenantId, string currentNodeId)>();

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using (var selectCmd = new NpgsqlCommand(
            $"SELECT tenant_id, node_id FROM {_schema.Table("tenant_assignments")}",
            connection))
        await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                assignments.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (assignments.Count == 0)
            return 0;

        var ring = ConsistentHashRing.Build(activeNodeIds);
        var changedCount = 0;

        // Update any tenant whose assigned node has changed
        foreach (var (tenantId, currentNodeId) in assignments)
        {
            var newNodeId = ConsistentHashRing.GetNodeForTenant(ring, tenantId);
            if (newNodeId == currentNodeId)
                continue;

            await using var updateCmd = new NpgsqlCommand(
                $"""
                UPDATE {_schema.Table("tenant_assignments")}
                SET node_id = @nodeId,
                    assigned_at = now(),
                    ring_version = ring_version + 1
                WHERE tenant_id = @tenantId
                  AND node_id != @nodeId
                """,
                connection);

            updateCmd.Parameters.AddWithValue("nodeId", newNodeId);
            updateCmd.Parameters.AddWithValue("tenantId", tenantId);

            var rows = await updateCmd.ExecuteNonQueryAsync(ct);
            changedCount += rows;
        }

        return changedCount;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> GetAssignedTenantsAsync(string nodeId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT tenant_id FROM {_schema.Table("tenant_assignments")} WHERE node_id = @nodeId",
            connection);

        cmd.Parameters.AddWithValue("nodeId", nodeId);

        var result = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task RegisterTenantAsync(string tenantId, IReadOnlyList<string> activeNodeIds, CancellationToken ct = default)
    {
        if (activeNodeIds.Count == 0)
            return;

        var ring = ConsistentHashRing.Build(activeNodeIds);
        var nodeId = ConsistentHashRing.GetNodeForTenant(ring, tenantId);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema.Table("tenant_assignments")} (tenant_id, node_id)
            VALUES (@tenantId, @nodeId)
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            connection);

        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("nodeId", nodeId);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
