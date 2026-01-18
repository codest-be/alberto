using System.Data;
using System.Text.Json;
using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IStateStore{TState}"/>.
/// Stores state as JSONB, keyed by tenant + projection type + document ID + rebuild version.
/// </summary>
/// <typeparam name="TState">The type of state being stored.</typeparam>
public sealed class PostgresStateStore<TState> : IStateStore<TState>
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _tenantId;
    private readonly string _projectionType;
    private readonly SchemaQualifier _schema;
    private readonly int _rebuildVersion;

    public PostgresStateStore(
        NpgsqlDataSource dataSource,
        string tenantId,
        string? projectionType = null,
        string? schema = null,
        int rebuildVersion = 1)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _projectionType = projectionType ?? typeof(TState).Name;
        _schema = new SchemaQualifier(schema);
        _rebuildVersion = rebuildVersion;
    }

    public async Task<Dictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TState>();

        var result = new Dictionary<string, TState>();

        // Use transaction connection if provided, otherwise create new
        if (transaction is NpgsqlTransaction npgsqlTransaction)
        {
            await LoadWithConnectionAsync(npgsqlTransaction.Connection!, ids, result, ct);
        }
        else
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await LoadWithConnectionAsync(connection, ids, result, ct);
        }

        return result;
    }

    private async Task LoadWithConnectionAsync(
        NpgsqlConnection connection,
        List<string> documentIds,
        Dictionary<string, TState> result,
        CancellationToken ct)
    {
        // Build parameterized IN clause
        var parameters = new List<NpgsqlParameter>();
        var parameterNames = new List<string>();

        for (var i = 0; i < documentIds.Count; i++)
        {
            var paramName = $"@doc_{i}";
            parameterNames.Add(paramName);
            parameters.Add(new NpgsqlParameter(paramName, documentIds[i]));
        }

        var sql = $"""
            SELECT document_id, state
            FROM {_schema.Table("projection_states")}
            WHERE tenant_id = @tenant_id
              AND projection_type = @projection_type
              AND rebuild_version = @rebuild_version
              AND document_id IN ({string.Join(", ", parameterNames)})
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("tenant_id", _tenantId);
        cmd.Parameters.AddWithValue("projection_type", _projectionType);
        cmd.Parameters.AddWithValue("rebuild_version", _rebuildVersion);
        cmd.Parameters.AddRange(parameters.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var docId = reader.GetString(0);
            var stateJson = reader.GetString(1);
            var state = JsonSerializer.Deserialize<TState>(stateJson);
            if (state is not null)
            {
                result[docId] = state;
            }
        }
    }

    public async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        // Use transaction connection if provided, otherwise create new
        if (transaction is NpgsqlTransaction npgsqlTransaction)
        {
            await ApplyWithConnectionAsync(npgsqlTransaction.Connection!, upserts, deletes, ct);
        }
        else
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await ApplyWithConnectionAsync(connection, upserts, deletes, ct);
        }
    }

    private async Task ApplyWithConnectionAsync(
        NpgsqlConnection connection,
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct)
    {
        // Process upserts
        foreach (var (docId, state) in upserts)
        {
            var stateJson = JsonSerializer.Serialize(state);

            await using var cmd = new NpgsqlCommand(
                $"""
                INSERT INTO {_schema.Table("projection_states")} (tenant_id, projection_type, document_id, rebuild_version, state, updated_at)
                VALUES (@tenant_id, @projection_type, @document_id, @rebuild_version, @state::jsonb, now())
                ON CONFLICT (tenant_id, projection_type, document_id, rebuild_version) DO UPDATE
                SET state = @state::jsonb, updated_at = now()
                """,
                connection);

            cmd.Parameters.AddWithValue("tenant_id", _tenantId);
            cmd.Parameters.AddWithValue("projection_type", _projectionType);
            cmd.Parameters.AddWithValue("document_id", docId);
            cmd.Parameters.AddWithValue("rebuild_version", _rebuildVersion);
            cmd.Parameters.AddWithValue("state", stateJson);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Process deletes
        foreach (var docId in deletes)
        {
            await using var cmd = new NpgsqlCommand(
                $"""
                DELETE FROM {_schema.Table("projection_states")}
                WHERE tenant_id = @tenant_id
                  AND projection_type = @projection_type
                  AND document_id = @document_id
                  AND rebuild_version = @rebuild_version
                """,
                connection);

            cmd.Parameters.AddWithValue("tenant_id", _tenantId);
            cmd.Parameters.AddWithValue("projection_type", _projectionType);
            cmd.Parameters.AddWithValue("document_id", docId);
            cmd.Parameters.AddWithValue("rebuild_version", _rebuildVersion);

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<TState>> ListRecentAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        var result = new List<TState>();

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT state
            FROM {_schema.Table("projection_states")}
            WHERE tenant_id = @tenant_id
              AND projection_type = @projection_type
              AND rebuild_version = @rebuild_version
            ORDER BY updated_at DESC
            LIMIT @limit
            """,
            connection);

        cmd.Parameters.AddWithValue("tenant_id", _tenantId);
        cmd.Parameters.AddWithValue("projection_type", _projectionType);
        cmd.Parameters.AddWithValue("rebuild_version", _rebuildVersion);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var stateJson = reader.GetString(0);
            var state = JsonSerializer.Deserialize<TState>(stateJson);
            if (state is not null)
            {
                result.Add(state);
            }
        }

        return result;
    }
}
