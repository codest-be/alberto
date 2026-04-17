using System.Reflection;
using DbUp;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Handles database migrations for the PostgreSQL event store.
/// </summary>
public static class PostgresMigrator
{
    private const string MultiTenantScriptFolder = "Migrations";
    private const string SingleTenantScriptFolder = "Migrations.SingleTenant";

    /// <summary>
    /// Runs all pending migrations against the specified database.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="schema">Optional schema name. If provided, creates schema and qualifies all objects.</param>
    /// <param name="singleTenant">When true, runs single-tenant migrations (no tenant_id columns). Default is false (multi-tenant).</param>
    /// <returns>Migration result indicating success or failure.</returns>
    public static MigrationResult Migrate(string connectionString, string? schema = null, bool singleTenant = false)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        // Create schema if specified and doesn't exist
        if (!string.IsNullOrWhiteSpace(schema))
        {
            EnsureSchemaExists(connectionString, schema);
        }

        // Determine schema values for substitution
        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        var schemaPrefix = string.IsNullOrWhiteSpace(schema) ? "" : $"{schema}.";

        // Use schema-specific journal table so each module tracks migrations independently
        var journalSchema = string.IsNullOrWhiteSpace(schema) ? "public" : schema;

        var scriptFolder = singleTenant ? SingleTenantScriptFolder : MultiTenantScriptFolder;

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                scriptName => IsInFolder(scriptName, scriptFolder))
            .WithTransactionPerScript()
            .LogToConsole()
            .WithVariable("schema", schemaName)
            .WithVariable("schema_prefix", schemaPrefix)
            .JournalToPostgresqlTable(journalSchema, "schemaversions")
            .Build();

        var result = upgrader.PerformUpgrade();

        return new MigrationResult(
            result.Successful,
            result.Scripts.Select(s => s.Name).ToArray(),
            result.Error);
    }

    /// <summary>
    /// Gets a list of pending migrations that have not been applied.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="schema">Optional schema name for variable substitution.</param>
    /// <param name="singleTenant">When true, checks single-tenant migrations. Default is false (multi-tenant).</param>
    /// <returns>Names of pending migration scripts.</returns>
    public static IReadOnlyCollection<string> GetPendingMigrations(string connectionString, string? schema = null, bool singleTenant = false)
    {
        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        var schemaPrefix = string.IsNullOrWhiteSpace(schema) ? "" : $"{schema}.";

        var scriptFolder = singleTenant ? SingleTenantScriptFolder : MultiTenantScriptFolder;

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                scriptName => IsInFolder(scriptName, scriptFolder))
            .WithVariable("schema", schemaName)
            .WithVariable("schema_prefix", schemaPrefix)
            .Build();

        return upgrader.GetScriptsToExecute()
            .Select(s => s.Name)
            .ToArray();
    }

    private static bool IsInFolder(string scriptName, string folderPath)
    {
        // Script names are like "Alberto.Dcb.Postgres.Migrations.001_InitialSchema.sql"
        // or "Alberto.Dcb.Postgres.Migrations.SingleTenant.001_InitialSchema.sql"
        var prefix = $"Alberto.Dcb.Postgres.{folderPath}.";

        if (!scriptName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Exclude nested subdirectories (e.g., exclude SingleTenant scripts when filtering for Migrations)
        if (folderPath == MultiTenantScriptFolder)
        {
            // Exclude anything that has a further dot-separated segment indicating a subdirectory
            var remainder = scriptName.Substring(prefix.Length);
            // If it contains another folder segment before the file extension, it's in a subdirectory
            // e.g., "SingleTenant.001_InitialSchema.sql" would be excluded
            return !remainder.StartsWith("SingleTenant.", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static void EnsureSchemaExists(string connectionString, string schema)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {schema}";
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Result of a migration operation.
/// </summary>
/// <param name="Successful">Whether the migration succeeded.</param>
/// <param name="ExecutedScripts">Names of scripts that were executed.</param>
/// <param name="Error">Exception if migration failed, null otherwise.</param>
public record MigrationResult(
    bool Successful,
    IReadOnlyCollection<string> ExecutedScripts,
    Exception? Error);
