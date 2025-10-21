using System.Reflection;
using Alberto.EventSourcing.Projections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Runtime migration service that generates, persists, and runs SQL migrations.
/// First run: Generates SQL files and saves to disk
/// Subsequent runs: Loads SQL files from disk
/// Uses IHostedLifecycleService to ensure migrations complete before other services start
/// </summary>
public sealed class MigrationHostedService(
    PostgresSchemaRegistry schemaRegistry,
    ILogger<MigrationHostedService> logger)
    : IHostedLifecycleService
{
    private const string MigrationsFolder = "Migrations/Generated";

    private readonly ILogger<MigrationHostedService>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly PostgresSchemaRegistry _schemaRegistry =
        schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting runtime database migrations");

        try
        {
            // Get schemas from registry (populated during module registration)
            var schemaConnectionMap = _schemaRegistry.GetAll();

            if (schemaConnectionMap.Count == 0)
            {
                _logger.LogWarning("No EventStore schemas registered");
                return;
            }

            _logger.LogInformation("Discovered {Count} schema(s): {Schemas}",
                schemaConnectionMap.Count, string.Join(", ", schemaConnectionMap.Keys));

            // Discover projections from DI container
            var projections = DiscoverProjections();
            _logger.LogInformation("Discovered {Count} projection(s)", projections.Count);

            // Ensure migrations folder exists
            Directory.CreateDirectory(MigrationsFolder);

            // Run migrations for each schema
            foreach (var (schema, connectionString) in schemaConnectionMap.OrderBy(kv => kv.Key))
            {
                await MigrateSchemaAsync(schema, connectionString, projections, cancellationToken);
            }

            _logger.LogInformation("All database migrations completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during database migrations");
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private List<ProjectionInfo> DiscoverProjections()
    {
        var projections = new List<ProjectionInfo>();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => t.GetInterfaces()
                        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name == "IProjector`1"))
                    .ToList();

                foreach (var projectorType in types)
                {
                    // Get TState from IProjector<TState>
                    var projectorInterface = projectorType.GetInterfaces()
                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().Name == "IProjector`1");

                    if (projectorInterface == null) continue;

                    var stateType = projectorInterface.GetGenericArguments()[0];

                    // Check for GenerateMigration attribute
                    var attr = projectorType.GetCustomAttribute<GenerateMigrationAttribute>();
                    if (attr != null && !string.IsNullOrEmpty(attr.Schema))
                    {
                        // Use ProjectionTableNameResolver to ensure consistency with runtime repository
                        var tableName = ProjectionTableNameResolver.ResolveTableName(projectorType, stateType);

                        projections.Add(new ProjectionInfo(
                            attr.Schema,
                            tableName,
                            stateType.Name));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect assembly {Assembly}", assembly.FullName);
            }
        }

        return projections;
    }

    private async Task MigrateSchemaAsync(
        string schema,
        string connectionString,
        List<ProjectionInfo> allProjections,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Migrating schema: {Schema}", schema);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Ensure tracking table exists
        await EnsureMigrationTrackingTableAsync(connection, schema, cancellationToken);

        // 1. EventStore migration
        await RunMigrationAsync(connection, schema, $"001_EventStore_{schema}",
            () => GenerateEventStoreSql(schema), cancellationToken);

        // 2. Projection schema migration (if projections exist for this schema)
        var schemaProjections = allProjections.Where(p =>
            string.Equals(p.Schema, schema, StringComparison.OrdinalIgnoreCase)).ToList();

        if (schemaProjections.Count > 0)
        {
            await RunMigrationAsync(connection, schema, $"002_ProjectionsSchema_{schema}",
                () => GenerateProjectionSchemaSql(schema), cancellationToken);

            // 3. Individual projection table migrations
            int migrationNumber = 3;
            foreach (var projection in schemaProjections.OrderBy(p => p.TableName))
            {
                await RunMigrationAsync(connection, schema,
                    $"{migrationNumber:D3}_ProjectionTable_{schema}_{projection.TableName}",
                    () => GenerateProjectionTableSql(schema, projection.TableName),
                    cancellationToken);
                migrationNumber++;
            }
        }

        _logger.LogInformation("Schema migration completed: {Schema}", schema);
    }

    private async Task RunMigrationAsync(
        NpgsqlConnection connection,
        string schema,
        string migrationKey,
        Func<string> sqlGenerator,
        CancellationToken cancellationToken)
    {
        // Check if already applied
        if (await IsMigrationAppliedAsync(connection, schema, migrationKey, cancellationToken))
        {
            _logger.LogDebug("Skipping already applied migration: {Key}", migrationKey);
            return;
        }

        // Get or generate SQL
        var sqlFilePath = Path.Combine(MigrationsFolder, $"{migrationKey}.sql");
        string sql;

        if (File.Exists(sqlFilePath))
        {
            sql = await File.ReadAllTextAsync(sqlFilePath, cancellationToken);
            _logger.LogDebug("Loaded migration from disk: {Key}", migrationKey);
        }
        else
        {
            sql = sqlGenerator();
            await File.WriteAllTextAsync(sqlFilePath, sql, cancellationToken);
            _logger.LogInformation("Generated and saved migration: {Key}", migrationKey);
        }

        // Replace schema placeholder
        sql = sql.Replace("{schema}", schema);

        // Run migration
        _logger.LogInformation("Executing migration: {Key}", migrationKey);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await RecordMigrationAsync(connection, transaction, schema, migrationKey, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("Migration completed: {Key}", migrationKey);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Migration failed: {Key}", migrationKey);
            throw;
        }
    }

    private string GenerateEventStoreSql(string schema)
    {
        return $@"-- =============================================================================
-- ALBERTO EVENT STORE SCHEMA FOR POSTGRESQL
-- =============================================================================
-- Schema: {schema}
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS {schema};

-- Main events table with tenant support
CREATE TABLE IF NOT EXISTS {schema}.events
(
    position       BIGSERIAL PRIMARY KEY,
    id             UUID NOT NULL UNIQUE,
    tenant_id      VARCHAR(20) NOT NULL,
    event_type     TEXT NOT NULL,
    data           JSONB NOT NULL,
    tags           TEXT[] NOT NULL DEFAULT '{{}}',
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata       JSONB NOT NULL DEFAULT '{{}}'
);

-- Index for consistency checks: WHERE tenant_id = ? AND id = ?
-- Used by: Consistency boundary checks in append operations
CREATE INDEX IF NOT EXISTS idx_{schema}_events_tenant_id 
ON {schema}.events (tenant_id, id);

-- Primary covering index for tenant-scoped queries
-- Used by: Stream(tenant), Stream(tenant, tags), Stream(tenant, eventType, tags)
-- Covers: Most tenant queries with index-only scans for minority tenants
CREATE INDEX IF NOT EXISTS idx_{schema}_events_tenant_all 
ON {schema}.events (tenant_id, position)
INCLUDE (event_type, tags, id, data, metadata, created_at);

-- Global position index for subscription queries
-- Used by: StreamAll(fromPosition) - cross-tenant event streaming
-- Covers: Subscription queries with index-only scans
CREATE INDEX IF NOT EXISTS idx_{schema}_events_global_position 
ON {schema}.events (position) 
INCLUDE (id, tenant_id, event_type, tags, data, metadata, created_at);

-- Specialized index for event type filtering
-- Used by: Stream(tenant, eventType) when event_type is highly selective
-- Note: tenant_all can handle this too, but this is faster for event_type-first queries
CREATE INDEX IF NOT EXISTS idx_{schema}_events_tenant_type_position 
ON {schema}.events (tenant_id, event_type, position);

-- Subscription checkpoints
CREATE TABLE IF NOT EXISTS {schema}.subscription_checkpoints
(
    subscription_id VARCHAR PRIMARY KEY,
    position        BIGINT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_{schema}_subscription_checkpoints_updated ON {schema}.subscription_checkpoints (updated_at DESC);

-- Poison pills
CREATE TABLE IF NOT EXISTS {schema}.subscription_poison_pills
(
    id                  UUID PRIMARY KEY,
    subscription_id     VARCHAR NOT NULL,
    global_position     BIGINT NOT NULL,
    event_id            UUID NOT NULL,
    event_type          VARCHAR NOT NULL,
    event_data          JSONB NOT NULL,
    metadata            JSONB NOT NULL,
    error_message       TEXT NOT NULL,
    stack_trace         TEXT,
    retry_count         INT NOT NULL,
    first_failed_at     TIMESTAMPTZ NOT NULL,
    last_failed_at      TIMESTAMPTZ NOT NULL,
    resolved_at         TIMESTAMPTZ,
    resolved_by         VARCHAR,
    resolution_action   VARCHAR,
    resolution_notes    TEXT,
    UNIQUE (subscription_id, global_position)  -- Ensure global_position is unique within subscription
);

CREATE INDEX IF NOT EXISTS idx_{schema}_poison_pills_subscription ON {schema}.subscription_poison_pills (subscription_id);
CREATE INDEX IF NOT EXISTS idx_{schema}_poison_pills_event ON {schema}.subscription_poison_pills (event_id);
CREATE INDEX IF NOT EXISTS idx_{schema}_poison_pills_unresolved ON {schema}.subscription_poison_pills (subscription_id, last_failed_at) WHERE resolved_at IS NULL;
";
    }

    private string GenerateProjectionSchemaSql(string schema)
    {
        return $@"-- =============================================================================
-- ALBERTO PROJECTIONS SCHEMA FOR POSTGRESQL
-- =============================================================================
-- Schema: {schema}
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS {schema};
";
    }

    private string GenerateProjectionTableSql(string schema, string tableName)
    {
        return $@"-- =============================================================================
-- PROJECTION TABLE: {schema}.{tableName}
-- =============================================================================

CREATE TABLE IF NOT EXISTS {schema}.{tableName} (
    tenant_id TEXT NOT NULL,
    key TEXT NOT NULL,
    state JSONB NOT NULL,
    global_version BIGINT NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, key)
);

CREATE INDEX IF NOT EXISTS idx_{schema}_{tableName}_tenant_updated ON {schema}.{tableName}(tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS idx_{schema}_{tableName}_global_version ON {schema}.{tableName}(tenant_id, key, global_version);

COMMENT ON TABLE {schema}.{tableName} IS 'Projection state for {tableName}';
";
    }

    private async Task EnsureMigrationTrackingTableAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        var sql = $@"
            CREATE SCHEMA IF NOT EXISTS {schema};

            CREATE TABLE IF NOT EXISTS {schema}.__migrations (
                migration_key VARCHAR(255) PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            -- Drop migration_number column if it exists (from old schema)
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = '{schema}'
                    AND table_name = '__migrations'
                    AND column_name = 'migration_number'
                ) THEN
                    ALTER TABLE {schema}.__migrations DROP COLUMN migration_number;
                END IF;
            END $$;

            CREATE INDEX IF NOT EXISTS idx_{schema}_migrations_applied
                ON {schema}.__migrations (applied_at DESC);
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> IsMigrationAppliedAsync(
        NpgsqlConnection connection,
        string schema,
        string migrationKey,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(*) FROM {schema}.__migrations WHERE migration_key = @key";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", migrationKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result) > 0;
    }

    private async Task RecordMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string migrationKey,
        CancellationToken cancellationToken)
    {
        var sql = $@"
            INSERT INTO {schema}.__migrations (migration_key, applied_at)
            VALUES (@key, NOW())
            ON CONFLICT (migration_key) DO NOTHING
        ";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("key", migrationKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private record ProjectionInfo(string Schema, string TableName, string TypeName);
}