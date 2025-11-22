using System.ComponentModel;
using System.Text.Json;
using Alberto.EventSourcing.Projections;
using Alberto.EventStore.MultiTenant;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Alberto.Projections.Postgres;

/// <summary>
/// PostgreSQL implementation of IProjectionRepository using JSONB for storage.
/// Provides high-performance, persistent projection storage for production scenarios.
/// </summary>
/// <typeparam name="TKey">The type of the projection key</typeparam>
/// <typeparam name="TState">The projected state type</typeparam>
public sealed class PostgresProjectionRepository<TKey, TState> : IProjectionRepository<TKey, TState>
    where TKey : notnull
    where TState : new()
{
    private static readonly JsonSerializerOptions DefaultJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly JsonSerializerOptions _jsonOptions;

    private readonly ILogger<PostgresProjectionRepository<TKey, TState>> _logger;
    private readonly PostgresProjectionOptions _options;
    private readonly string _schemaQualifiedTableName;
    private readonly string _tableName;
    private readonly ITenantContext _tenantContext;

    public PostgresProjectionRepository(
        IOptions<PostgresProjectionOptions> options,
        ILogger<PostgresProjectionRepository<TKey, TState>> logger,
        ITenantContext tenantContext,
        Type? projectorType = null)
    {
        _options = options.Value;
        _logger = logger;
        _tenantContext = tenantContext;
        _jsonOptions = _options.SerializerOptions ?? DefaultJsonOptions;

        // Use ProjectionTableNameResolver to ensure consistency with migration generator
        _tableName = projectorType != null
            ? ProjectionTableNameResolver.ResolveTableName(projectorType, typeof(TState))
            : typeof(TState).Name.ToLowerInvariant() + "_projections";

        // Build schema-qualified table name
        var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "default" : _options.Schema;
        _schemaQualifiedTableName = $"{schema}.{_tableName}";
    }

    /// <inheritdoc />
    public async Task<TState?> Get(TKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql = $"SELECT state FROM {_schemaQualifiedTableName} WHERE tenant_id = @TenantId AND key = @Key";
        var json = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString() },
                cancellationToken: cancellationToken));

        if (json is null)
            return default;

        return JsonSerializer.Deserialize<TState>(json, _jsonOptions);
    }

    /// <inheritdoc />
    public async Task<IDictionary<TKey, TState?>> BatchGet(
        IEnumerable<TKey> keys,
        CancellationToken cancellationToken = default)
    {
        var keyList = keys.ToList();
        if (keyList.Count == 0)
            return new Dictionary<TKey, TState?>();

        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        // Use ANY array query for efficient batch retrieval
        var keyStrings = keyList.Select(k => k.ToString()).ToArray();
        var sql = $"""
                   SELECT key, state, global_version
                   FROM {_schemaQualifiedTableName}
                   WHERE tenant_id = @TenantId AND key = ANY(@Keys)
                   """;

        var results = await connection.QueryAsync<(string Key, string State, long GlobalVersion)>(
            new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id, Keys = keyStrings },
                cancellationToken: cancellationToken));

        // Build dictionary with results, converting string keys back to TKey
        var resultDict = new Dictionary<TKey, TState?>();
        foreach (var (keyString, state, _) in results)
        {
            var key = ConvertStringToKey(keyString);
            resultDict[key] = JsonSerializer.Deserialize<TState>(state, _jsonOptions);
        }

        // Fill in missing keys with null
        foreach (var key in keyList)
        {
            if (!resultDict.ContainsKey(key))
            {
                resultDict[key] = default;
            }
        }

        return resultDict;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<TState>> GetAll(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql = $"SELECT state FROM {_schemaQualifiedTableName} WHERE tenant_id = @TenantId ORDER BY updated_at DESC";
        var jsonStates = await connection.QueryAsync<string>(
            new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id },
                cancellationToken: cancellationToken));

        return jsonStates
            .Select(json => JsonSerializer.Deserialize<TState>(json, _jsonOptions))
            .Where(state => state is not null)
            .Cast<TState>()
            .ToList();
    }

    /// <inheritdoc />
    public async Task Upsert(TKey key, TState state, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var json = JsonSerializer.Serialize(state, _jsonOptions);
        var sql = $"""
                   INSERT INTO {_schemaQualifiedTableName} (tenant_id, key, state, global_version, updated_at)
                   VALUES (@TenantId, @Key, @State::jsonb, 0, NOW())
                   ON CONFLICT (tenant_id, key) DO UPDATE
                   SET state = EXCLUDED.state, updated_at = NOW()
                   """;

        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString(), State = json },
                cancellationToken: cancellationToken));

        _logger.LogDebug("Upserted projection {Key} for tenant {TenantId} to table {TableName}", key,
            _tenantContext.Tenant.Id, _tableName);
    }

    /// <inheritdoc />
    public async Task Update(TKey key, Func<TState, TState> updateFn, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var getSql = $"""
                          SELECT state 
                          FROM {_schemaQualifiedTableName} 
                          WHERE tenant_id = @TenantId AND key = @Key
                          FOR UPDATE
                          """;

            var currentJson = await connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    getSql,
                    new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString() },
                    transaction: transaction,
                    cancellationToken: cancellationToken));

            var currentState = currentJson is null
                ? new TState()
                : JsonSerializer.Deserialize<TState>(currentJson, _jsonOptions) ?? new TState();

            // Apply update
            var newState = updateFn(currentState);
            var json = JsonSerializer.Serialize(newState, _jsonOptions);

            // Upsert
            var upsertSql = $"""
                             INSERT INTO {_schemaQualifiedTableName} (tenant_id, key, state, global_version, updated_at)
                             VALUES (@TenantId, @Key, @State::jsonb, 0, NOW())
                             ON CONFLICT (tenant_id, key) DO UPDATE
                             SET state = EXCLUDED.state,
                                 updated_at = NOW()
                             """;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    upsertSql,
                    new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString(), State = json },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateWithVersion(TKey key, Func<TState, TState> updateFn, long globalVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Get current state and version with row lock
            var getCurrentSql = $"""
                                 SELECT state, global_version 
                                 FROM {_schemaQualifiedTableName} 
                                 WHERE tenant_id = @TenantId AND key = @Key
                                 FOR UPDATE
                                 """;

            var current = await connection.QuerySingleOrDefaultAsync<(string? State, long GlobalVersion)>(
                new CommandDefinition(getCurrentSql, new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString() },
                    transaction, cancellationToken: cancellationToken));

            // Check if we should skip this event (already processed or out of order)
            if (current.State is not null && current.GlobalVersion >= globalVersion)
            {
                _logger.LogDebug(
                    "Skipping projection update for {Key} - event version {EventVersion} <= stored version {StoredVersion}",
                    key, globalVersion, current.GlobalVersion);
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // Deserialize current state or create new
            var currentState = current.State is not null
                ? JsonSerializer.Deserialize<TState>(current.State, _jsonOptions) ?? new TState()
                : new TState();

            // Apply update
            var newState = updateFn(currentState);
            var json = JsonSerializer.Serialize(newState, _jsonOptions);

            // Upsert with version check
            var upsertSql = $"""
                             INSERT INTO {_schemaQualifiedTableName} (tenant_id, key, state, global_version, updated_at)
                             VALUES (@TenantId, @Key, @State::jsonb, @GlobalVersion, NOW())
                             ON CONFLICT (tenant_id, key) DO UPDATE
                             SET state = EXCLUDED.state, 
                                 global_version = EXCLUDED.global_version,
                                 updated_at = NOW()
                             WHERE {_schemaQualifiedTableName}.global_version < EXCLUDED.global_version
                             """;

            var rowsAffected = await connection.ExecuteAsync(
                new CommandDefinition(upsertSql,
                    new
                    {
                        TenantId = _tenantContext.Tenant.Id, Key = key.ToString(), State = json,
                        GlobalVersion = globalVersion
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            if (rowsAffected > 0)
            {
                _logger.LogDebug(
                    "Updated projection {Key} for tenant {TenantId} to version {GlobalVersion}",
                    key, _tenantContext.Tenant.Id, globalVersion);
                return true;
            }

            _logger.LogDebug(
                "Skipped projection update for {Key} - concurrent update detected",
                key);
            return false;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task Delete(TKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql = $"DELETE FROM {_schemaQualifiedTableName} WHERE tenant_id = @TenantId AND key = @Key";
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString() },
                cancellationToken: cancellationToken));

        _logger.LogDebug("Deleted projection {Key} for tenant {TenantId} from table {TableName}", key,
            _tenantContext.Tenant.Id, _tableName);
    }

    /// <inheritdoc />
    public async Task<bool> Exists(TKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql =
            $"SELECT EXISTS(SELECT 1 FROM {_schemaQualifiedTableName} WHERE tenant_id = @TenantId AND key = @Key)";
        return await connection.QuerySingleAsync<bool>(
            new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString() },
                cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task Clear(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql = $"DELETE FROM {_schemaQualifiedTableName} WHERE tenant_id = @TenantId";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id },
            cancellationToken: cancellationToken));

        _logger.LogWarning("Cleared all projections for tenant {TenantId} from table {TableName}",
            _tenantContext.Tenant.Id, _tableName);
    }

    /// <inheritdoc />
    public async Task<int> BatchUpsertWithVersion(
        IDictionary<TKey, (TState State, long Version)> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0)
            return 0;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var rowsAffected = 0;

            // Use UNNEST for efficient batch insert
            // Build arrays of values
            var keys = new List<string>();
            var states = new List<string>();
            var versions = new List<long>();

            foreach (var (key, (state, version)) in updates)
            {
                keys.Add(key.ToString()!);
                states.Add(JsonSerializer.Serialize(state, _jsonOptions));
                versions.Add(version);
            }

            var sql = $"""
                       INSERT INTO {_schemaQualifiedTableName} (tenant_id, key, state, global_version, updated_at)
                       SELECT
                           @TenantId,
                           unnest(@Keys::text[]),
                           unnest(@States::jsonb[]),
                           unnest(@Versions::bigint[]),
                           NOW()
                       ON CONFLICT (tenant_id, key) DO UPDATE
                       SET state = EXCLUDED.state,
                           global_version = EXCLUDED.global_version,
                           updated_at = NOW()
                       WHERE {_schemaQualifiedTableName}.global_version < EXCLUDED.global_version
                       """;

            rowsAffected = await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        TenantId = _tenantContext.Tenant.Id, Keys = keys.ToArray(), States = states.ToArray(),
                        Versions = versions.ToArray()
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug(
                "Batch upserted {Count} projections for tenant {TenantId}, {RowsAffected} rows affected",
                updates.Count,
                _tenantContext.Tenant.Id,
                rowsAffected);

            return rowsAffected;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static TKey ConvertStringToKey(string keyString)
    {
        var keyType = typeof(TKey);

        // Handle common key types
        if (keyType == typeof(string))
            return (TKey)(object)keyString;

        if (keyType == typeof(Guid))
            return (TKey)(object)Guid.Parse(keyString);

        if (keyType == typeof(int))
            return (TKey)(object)int.Parse(keyString);

        if (keyType == typeof(long))
            return (TKey)(object)long.Parse(keyString);

        // Fallback: use type converter
        var converter = TypeDescriptor.GetConverter(keyType);
        if (converter.CanConvertFrom(typeof(string)))
        {
            return (TKey)converter.ConvertFromString(keyString)!;
        }

        throw new NotSupportedException($"Key type {keyType.Name} is not supported for conversion from string");
    }
}