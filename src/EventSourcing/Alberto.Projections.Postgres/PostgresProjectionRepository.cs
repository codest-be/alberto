using System.Text.Json;
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
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ILogger<PostgresProjectionRepository<TKey, TState>> _logger;
    private readonly PostgresProjectionOptions _options;
    private readonly string _schemaQualifiedTableName;
    private readonly string _tableName;
    private readonly ITenantContext _tenantContext;

    public PostgresProjectionRepository(
        IOptions<PostgresProjectionOptions> options,
        ILogger<PostgresProjectionRepository<TKey, TState>> logger,
        ITenantContext tenantContext)
    {
        _options = options.Value;
        _logger = logger;
        _tenantContext = tenantContext;
        _tableName = typeof(TState).Name.ToLowerInvariant() + "_projections";

        // Build schema-qualified table name
        var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "default" : _options.Schema;
        _schemaQualifiedTableName = $"{schema}.{_tableName}";

        // Always initialize the projection table (per-projection table creation)
        InitializeTable().GetAwaiter().GetResult();
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Get current state
            var currentState = await Get(key, cancellationToken) ?? new TState();

            // Apply update
            var newState = updateFn(currentState);

            // Upsert
            await Upsert(key, newState, cancellationToken);

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
                        TenantId = _tenantContext.Tenant.Id,
                        Key = key.ToString(),
                        State = json,
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

    private async Task InitializeTable()
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync();

        var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "public" : _options.Schema;

        // Create schema if it doesn't exist
        var createSchema = $"CREATE SCHEMA IF NOT EXISTS {schema}";
        await connection.ExecuteAsync(createSchema);

        var sql = $"""
                   CREATE TABLE IF NOT EXISTS {_schemaQualifiedTableName} (
                       tenant_id TEXT NOT NULL,
                       key TEXT NOT NULL,
                       state JSONB NOT NULL,
                       global_version BIGINT NOT NULL DEFAULT 0,
                       updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                       PRIMARY KEY (tenant_id, key)
                   );

                   CREATE INDEX IF NOT EXISTS idx_{_tableName}_tenant_updated ON {_schemaQualifiedTableName}(tenant_id, updated_at);
                   CREATE INDEX IF NOT EXISTS idx_{_tableName}_global_version ON {_schemaQualifiedTableName}(tenant_id, key, global_version);
                   """;

        await connection.ExecuteAsync(sql);
        _logger.LogDebug("Initialized projection table {TableName} in schema {Schema}", _tableName, schema);
    }
}