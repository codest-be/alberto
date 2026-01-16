using System.Reflection;
using DbUp;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Handles database migrations for the PostgreSQL event store.
/// </summary>
public static class PostgresMigrator
{
    /// <summary>
    /// Runs all pending migrations against the specified database.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="schema">Optional schema name. If provided, creates schema and qualifies all objects.</param>
    /// <returns>True if migrations succeeded, false otherwise.</returns>
    public static MigrationResult Migrate(string connectionString, string? schema = null)
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

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
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
    /// <returns>Names of pending migration scripts.</returns>
    public static IReadOnlyCollection<string> GetPendingMigrations(string connectionString, string? schema = null)
    {
        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        var schemaPrefix = string.IsNullOrWhiteSpace(schema) ? "" : $"{schema}.";

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .WithVariable("schema", schemaName)
            .WithVariable("schema_prefix", schemaPrefix)
            .Build();

        return upgrader.GetScriptsToExecute()
            .Select(s => s.Name)
            .ToArray();
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
