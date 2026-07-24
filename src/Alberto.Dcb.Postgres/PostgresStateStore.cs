using System.Data;
using System.Text.Json;
using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IStateStore{TState}"/>.
/// Stores state as JSONB, keyed by projection type + document ID + rebuild version.
/// </summary>
/// <remarks>
/// Pass <paramref name="tenantId"/> to enable multi-tenant mode: every DML statement is
/// scoped to that tenant via the <c>tenant_id</c> column on
/// <c>alberto_projection_states</c>. Omit (or pass <see langword="null"/>) for
/// single-tenant deployments; the column is then absent from all queries and
/// the single-tenant primary key constraint is used.
/// </remarks>
public sealed class PostgresStateStore<TState>(
    NpgsqlDataSource dataSource,
    string? projectionType = null,
    string? schema = null,
    int rebuildVersion = 1,
    string? tenantId = null)
    : IStateStore<TState>
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly string _projectionType = projectionType ?? typeof(TState).Name;
    private readonly SchemaQualifier _schema = new(schema);
    private readonly bool _multiTenant = tenantId is not null;
    private readonly string? _tenantId = tenantId;

    /// <inheritdoc/>
    public async Task<Dictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TState>();

        var result = new Dictionary<string, TState>();

        if (transaction is NpgsqlTransaction npgsqlTransaction)
            await LoadWithConnectionAsync(npgsqlTransaction.Connection!, ids, result, ct);
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
        // = ANY(@document_ids) with a typed array parameter allows the query plan to be
        // cached regardless of how many document IDs are requested, unlike a variable-length
        // IN (param0, param1, ...) list which produces a different plan per cardinality.
        string sql;
        if (_multiTenant)
        {
            sql = $"""
                SELECT document_id, state
                FROM {_schema.Table("alberto_projection_states")}
                WHERE tenant_id = @tenant_id
                  AND projection_type = @projection_type
                  AND rebuild_version = @rebuild_version
                  AND document_id = ANY(@document_ids)
                """;
        }
        else
        {
            sql = $"""
                SELECT document_id, state
                FROM {_schema.Table("alberto_projection_states")}
                WHERE projection_type = @projection_type
                  AND rebuild_version = @rebuild_version
                  AND document_id = ANY(@document_ids)
                """;
        }

        await using var cmd = new NpgsqlCommand(sql, connection);

        if (_multiTenant)
            cmd.Parameters.AddWithValue("tenant_id", _tenantId!);

        cmd.Parameters.AddWithValue("projection_type", _projectionType);
        cmd.Parameters.AddWithValue("rebuild_version", rebuildVersion);
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("document_ids", documentIds.ToArray()));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var docId = reader.GetString(0);
            var stateJson = reader.GetString(1);
            var state = JsonSerializer.Deserialize<TState>(stateJson);
            if (state is not null)
                result[docId] = state;
        }
    }

    /// <inheritdoc/>
    public async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        if (transaction is NpgsqlTransaction npgsqlTransaction)
            await ApplyWithConnectionAsync(npgsqlTransaction.Connection!, npgsqlTransaction, upserts, deletes, ct);
        else
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await ApplyWithConnectionAsync(connection, externalTransaction: null, upserts, deletes, ct);
        }
    }

    private async Task ApplyWithConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? externalTransaction,
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct)
    {
        // When no caller-supplied transaction is present, wrap all batch commands in
        // our own transaction so the upserts + deletes land atomically.
        NpgsqlTransaction? ownedTransaction = externalTransaction is null
            ? await connection.BeginTransactionAsync(ct)
            : null;

        try
        {
            string upsertSql;
            string deleteSql;

            if (_multiTenant)
            {
                upsertSql = $"""
                    INSERT INTO {_schema.Table("alberto_projection_states")}
                        (tenant_id, projection_type, document_id, rebuild_version, state, updated_at)
                    VALUES (@tenant_id, @projection_type, @document_id, @rebuild_version, @state::jsonb, now())
                    ON CONFLICT (tenant_id, projection_type, document_id, rebuild_version) DO UPDATE
                    SET state = @state::jsonb, updated_at = now()
                    """;

                deleteSql = $"""
                    DELETE FROM {_schema.Table("alberto_projection_states")}
                    WHERE tenant_id = @tenant_id
                      AND projection_type = @projection_type
                      AND document_id = @document_id
                      AND rebuild_version = @rebuild_version
                    """;
            }
            else
            {
                upsertSql = $"""
                    INSERT INTO {_schema.Table("alberto_projection_states")}
                        (projection_type, document_id, rebuild_version, state, updated_at)
                    VALUES (@projection_type, @document_id, @rebuild_version, @state::jsonb, now())
                    ON CONFLICT (projection_type, document_id, rebuild_version) DO UPDATE
                    SET state = @state::jsonb, updated_at = now()
                    """;

                deleteSql = $"""
                    DELETE FROM {_schema.Table("alberto_projection_states")}
                    WHERE projection_type = @projection_type
                      AND document_id = @document_id
                      AND rebuild_version = @rebuild_version
                    """;
            }

            await using var batch = new NpgsqlBatch(connection)
            {
                Transaction = externalTransaction ?? ownedTransaction
            };

            foreach (var (docId, state) in upserts)
            {
                var stateJson = JsonSerializer.Serialize(state);
                var batchCmd = new NpgsqlBatchCommand(upsertSql);
                if (_multiTenant)
                    batchCmd.Parameters.AddWithValue("tenant_id", _tenantId!);
                batchCmd.Parameters.AddWithValue("projection_type", _projectionType);
                batchCmd.Parameters.AddWithValue("document_id", docId);
                batchCmd.Parameters.AddWithValue("rebuild_version", rebuildVersion);
                batchCmd.Parameters.AddWithValue("state", stateJson);
                batch.BatchCommands.Add(batchCmd);
            }

            foreach (var docId in deletes)
            {
                var batchCmd = new NpgsqlBatchCommand(deleteSql);
                if (_multiTenant)
                    batchCmd.Parameters.AddWithValue("tenant_id", _tenantId!);
                batchCmd.Parameters.AddWithValue("projection_type", _projectionType);
                batchCmd.Parameters.AddWithValue("document_id", docId);
                batchCmd.Parameters.AddWithValue("rebuild_version", rebuildVersion);
                batch.BatchCommands.Add(batchCmd);
            }

            if (batch.BatchCommands.Count > 0)
                await batch.ExecuteNonQueryAsync(ct);

            if (ownedTransaction is not null)
                await ownedTransaction.CommitAsync(ct);
        }
        catch
        {
            if (ownedTransaction is not null)
                await ownedTransaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
                await ownedTransaction.DisposeAsync();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TState>> ListRecentAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        var result = new List<TState>();

        string sql;
        if (_multiTenant)
        {
            sql = $"""
                SELECT state
                FROM {_schema.Table("alberto_projection_states")}
                WHERE tenant_id = @tenant_id
                  AND projection_type = @projection_type
                  AND rebuild_version = @rebuild_version
                ORDER BY updated_at DESC
                LIMIT @limit
                """;
        }
        else
        {
            sql = $"""
                SELECT state
                FROM {_schema.Table("alberto_projection_states")}
                WHERE projection_type = @projection_type
                  AND rebuild_version = @rebuild_version
                ORDER BY updated_at DESC
                LIMIT @limit
                """;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, connection);

        if (_multiTenant)
            cmd.Parameters.AddWithValue("tenant_id", _tenantId!);

        cmd.Parameters.AddWithValue("projection_type", _projectionType);
        cmd.Parameters.AddWithValue("rebuild_version", rebuildVersion);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var stateJson = reader.GetString(0);
            var state = JsonSerializer.Deserialize<TState>(stateJson);
            if (state is not null)
                result.Add(state);
        }

        return result;
    }
}
