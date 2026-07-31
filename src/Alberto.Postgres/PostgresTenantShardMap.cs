using System.Diagnostics.CodeAnalysis;
using Alberto.Tenancy;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// PostgreSQL-backed tenant → shard catalog. Reads and writes
/// <c>alberto_tenant_shards</c> in the control database.
/// </summary>
/// <remarks>
/// One table in one database, holding shard <em>ids</em> and nothing else. What an id resolves to
/// stays in configuration, so a dump of this table leaks no credentials and a shard can be moved
/// to another host without touching a row.
/// </remarks>
/// <param name="dataSource">A data source for the control database.</param>
/// <param name="moduleKey">
/// The logical module key. Part of the primary key, so several modules can share one control
/// database without their tenants colliding.
/// </param>
/// <param name="schema">The schema the catalog table lives in, or null for the default.</param>
[Experimental("ALB9001")]
public sealed class PostgresTenantShardMap(
    NpgsqlDataSource dataSource,
    string moduleKey,
    string? schema = null) : ITenantShardMap
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly string _moduleKey = moduleKey ?? throw new ArgumentNullException(nameof(moduleKey));
    private readonly SchemaQualifier _schema = new(schema);

    /// <inheritdoc />
    public async ValueTask<string?> ResolveAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT shard_id
            FROM {_schema.Table("alberto_tenant_shards")}
            WHERE module_key = @moduleKey AND tenant_id = @tenantId
            """;
        command.Parameters.AddWithValue("moduleKey", _moduleKey);
        command.Parameters.AddWithValue("tenantId", tenantId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    /// <inheritdoc />
    public async ValueTask<string> AssignAsync(
        string tenantId, string shardId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        // DO NOTHING rather than DO UPDATE: two hosts seeing the same new tenant at the same
        // instant must agree on one shard, and the loser must learn which one won — writing a
        // tenant's events to two databases is unrecoverable. RETURNING is not enough on its own
        // because it yields nothing for the conflicting row, hence the second statement.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH inserted AS (
                INSERT INTO {_schema.Table("alberto_tenant_shards")} (module_key, tenant_id, shard_id)
                VALUES (@moduleKey, @tenantId, @shardId)
                ON CONFLICT (module_key, tenant_id) DO NOTHING
                RETURNING shard_id
            )
            SELECT shard_id FROM inserted
            UNION ALL
            SELECT shard_id FROM {_schema.Table("alberto_tenant_shards")}
            WHERE module_key = @moduleKey AND tenant_id = @tenantId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("moduleKey", _moduleKey);
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("shardId", shardId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string
            ?? throw new InvalidOperationException(
                $"Assigning tenant '{tenantId}' of module '{_moduleKey}' to shard '{shardId}' " +
                "returned no assignment.");
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, string>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT tenant_id, shard_id
            FROM {_schema.Table("alberto_tenant_shards")}
            WHERE module_key = @moduleKey
            """;
        command.Parameters.AddWithValue("moduleKey", _moduleKey);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            map[reader.GetString(0)] = reader.GetString(1);

        return map;
    }
}
