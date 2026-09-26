using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Alberto.Subscriptions;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IStateStore{TState}"/>.
/// Stores state as JSONB, keyed by projection type + document ID + rebuild version.
/// </summary>
/// <remarks>
/// <para>
/// Pass <c>tenantId</c> to enable multi-tenant mode: every DML statement is
/// scoped to that tenant via the <c>tenant_id</c> column on
/// <c>alberto_projection_states</c>. Omit (or pass <see langword="null"/>) for
/// single-tenant deployments; the column is then absent from all queries and
/// the single-tenant primary key constraint is used.
/// </para>
/// <para>
/// <strong>The mode is decided by the schema, not by the caller's intent.</strong> A module that
/// declared <c>.WithTenancy()</c> is migrated with <c>tenant_id NOT NULL</c> and a primary key
/// that includes it, so a store built without a <c>tenantId</c> against that schema
/// names <c>ON CONFLICT (projection_type, document_id, rebuild_version)</c> — a constraint that
/// does not exist — and every write fails with <c>42P10</c>. The reverse mismatch fails with
/// <c>42703</c> on the missing column. Neither degrades quietly, but neither is caught at
/// startup either: a projection wired the wrong way round dead-letters every event it is given
/// while the rest of the module looks healthy. A projection that wants one document across all
/// tenants still passes a tenant id — see
/// <see cref="Alberto.Tenancy.TenantScope.CrossTenantFor"/>.
/// </para>
/// <para>
/// <c>rebuildVersion</c> is a live handle resolved on every operation rather than
/// captured at construction, because the version a projection reads and writes changes
/// underneath a long-lived store when a rebuild is promoted. Omit it for the overwhelmingly
/// common case of a projection that is never rebuilt; its <see cref="ProjectionVersion.Current"/>
/// then returns version 1 forever at no cost.
/// </para>
/// <para>
/// State is stored as JSON. The constructor that takes a <see cref="JsonTypeInfo{T}"/> uses that
/// contract — typically from a source-generated <c>JsonSerializerContext</c> — and is the one to
/// use under trimming or Native AOT. The other resolves the contract by reflection, with
/// System.Text.Json's default options.
/// </para>
/// </remarks>
public sealed class PostgresStateStore<TState> : IStateStore<TState>
{
    private const string ReflectionJsonMessage =
        "Serializes TState with reflection-based System.Text.Json. Under trimming or Native AOT, " +
        "use the constructor that takes a JsonTypeInfo<TState> from a JsonSerializerContext.";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _projectionType;
    private readonly SchemaQualifier _schema;
    private readonly bool _multiTenant;
    private readonly string? _tenantId;
    private readonly ProjectionVersion _rebuildVersion;
    private readonly Lazy<JsonTypeInfo<TState>> _jsonTypeInfo;

    /// <summary>
    /// Creates a store that serializes <typeparamref name="TState"/> with reflection-based
    /// System.Text.Json and its default options.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the required JsonTypeInfo<TState> parameter; " +
                        "no call binds to both.")]
    public PostgresStateStore(
        NpgsqlDataSource dataSource,
        string? projectionType = null,
        string? schema = null,
        ProjectionVersion rebuildVersion = default,
        string? tenantId = null)
        : this(
            dataSource,
            // Resolved on first use, as JsonSerializer.Serialize<TState> did before this existed,
            // so a TState System.Text.Json cannot handle still fails on the first write rather
            // than at construction.
            new Lazy<JsonTypeInfo<TState>>(
                static () => (JsonTypeInfo<TState>)JsonSerializerOptions.Default.GetTypeInfo(typeof(TState))),
            projectionType,
            schema,
            rebuildVersion,
            tenantId)
    {
    }

    /// <summary>
    /// Creates a store that serializes <typeparamref name="TState"/> with
    /// <paramref name="jsonTypeInfo"/>. Safe under trimming and Native AOT.
    /// </summary>
    /// <param name="dataSource">The database the projection state lives in.</param>
    /// <param name="jsonTypeInfo">The JSON contract for <typeparamref name="TState"/>, e.g. <c>MyContext.Default.OrderSummary</c>.</param>
    /// <param name="projectionType">The projection's name in storage. Defaults to <c>typeof(TState).Name</c>.</param>
    /// <param name="schema">The schema the Alberto tables live in. Defaults to <c>public</c>.</param>
    /// <param name="rebuildVersion">The live rebuild version. Omit for a projection that is never rebuilt.</param>
    /// <param name="tenantId">The tenant this store reads and writes. Omit on a single-tenant module.</param>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the required JsonTypeInfo<TState> parameter; " +
                        "no call binds to both.")]
    public PostgresStateStore(
        NpgsqlDataSource dataSource,
        JsonTypeInfo<TState> jsonTypeInfo,
        string? projectionType = null,
        string? schema = null,
        ProjectionVersion rebuildVersion = default,
        string? tenantId = null)
        : this(
            dataSource,
            new Lazy<JsonTypeInfo<TState>>(jsonTypeInfo ?? throw new ArgumentNullException(nameof(jsonTypeInfo))),
            projectionType,
            schema,
            rebuildVersion,
            tenantId)
    {
    }

    private PostgresStateStore(
        NpgsqlDataSource dataSource,
        Lazy<JsonTypeInfo<TState>> jsonTypeInfo,
        string? projectionType,
        string? schema,
        ProjectionVersion rebuildVersion,
        string? tenantId)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _jsonTypeInfo = jsonTypeInfo;
        _projectionType = projectionType ?? typeof(TState).Name;
        _schema = new SchemaQualifier(schema);
        _multiTenant = tenantId is not null;
        _tenantId = tenantId;
        _rebuildVersion = rebuildVersion;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TState>();

        var result = new Dictionary<string, TState>();

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await LoadWithConnectionAsync(connection, ids, result, ct);

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
        cmd.Parameters.AddWithValue("rebuild_version", _rebuildVersion.Current);
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("document_ids", documentIds.ToArray()));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var docId = reader.GetString(0);
            var stateJson = reader.GetString(1);
            var state = JsonSerializer.Deserialize(stateJson, _jsonTypeInfo.Value);
            if (state is not null)
                result[docId] = state;
        }
    }

    /// <inheritdoc/>
    public async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);

        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await ApplyWithConnectionAsync(connection, upserts, deletes, ct);
    }

    private async Task ApplyWithConnectionAsync(
        NpgsqlConnection connection,
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct)
    {
        // The adapter owns the transaction so the upserts + deletes land atomically.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Resolved once for the whole batch. Resolving per statement would let a promotion
        // landing mid-batch split the upserts and deletes across two versions.
        var version = _rebuildVersion.Current;

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
                Transaction = transaction
            };

            foreach (var (docId, state) in upserts)
            {
                var stateJson = JsonSerializer.Serialize(state, _jsonTypeInfo.Value);
                var batchCmd = new NpgsqlBatchCommand(upsertSql);
                if (_multiTenant)
                    batchCmd.Parameters.AddWithValue("tenant_id", _tenantId!);
                batchCmd.Parameters.AddWithValue("projection_type", _projectionType);
                batchCmd.Parameters.AddWithValue("document_id", docId);
                batchCmd.Parameters.AddWithValue("rebuild_version", version);
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
                batchCmd.Parameters.AddWithValue("rebuild_version", version);
                batch.BatchCommands.Add(batchCmd);
            }

            if (batch.BatchCommands.Count > 0)
                await batch.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
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
        cmd.Parameters.AddWithValue("rebuild_version", _rebuildVersion.Current);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var stateJson = reader.GetString(0);
            var state = JsonSerializer.Deserialize(stateJson, _jsonTypeInfo.Value);
            if (state is not null)
                result.Add(state);
        }

        return result;
    }
}
