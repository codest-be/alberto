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
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<PostgresProjectionRepository<TKey, TState>> _logger;
    private readonly PostgresProjectionOptions _options;
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

        InitializeTable().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<TState?> Get(TKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql = $"SELECT state FROM {_tableName} WHERE tenant_id = @TenantId AND key = @Key";
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

        var sql = $"SELECT state FROM {_tableName} WHERE tenant_id = @TenantId ORDER BY updated_at DESC";
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
                   INSERT INTO {_tableName} (tenant_id, key, state, updated_at)
                   VALUES (@TenantId, @Key, @State::jsonb, NOW())
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
    public async Task Delete(TKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql = $"DELETE FROM {_tableName} WHERE tenant_id = @TenantId AND key = @Key";
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

        var sql = $"SELECT EXISTS(SELECT 1 FROM {_tableName} WHERE tenant_id = @TenantId AND key = @Key)";
        return await connection.QuerySingleAsync<bool>(
            new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id, Key = key.ToString() },
                cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task Clear(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);

        var sql = $"DELETE FROM {_tableName} WHERE tenant_id = @TenantId";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { TenantId = _tenantContext.Tenant.Id },
            cancellationToken: cancellationToken));

        _logger.LogWarning("Cleared all projections for tenant {TenantId} from table {TableName}",
            _tenantContext.Tenant.Id, _tableName);
    }

    private async Task InitializeTable()
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync();

        var sql = $"""
                   CREATE TABLE IF NOT EXISTS {_tableName} (
                       tenant_id TEXT NOT NULL,
                       key TEXT NOT NULL,
                       state JSONB NOT NULL,
                       updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                       PRIMARY KEY (tenant_id, key)
                   );

                   CREATE INDEX IF NOT EXISTS idx_{_tableName}_tenant_updated ON {_tableName}(tenant_id, updated_at);
                   """;

        await connection.ExecuteAsync(sql);
        _logger.LogDebug("Initialized projection table {TableName}", _tableName);
    }
}