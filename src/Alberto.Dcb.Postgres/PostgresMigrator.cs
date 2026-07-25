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
        // Validate schema name before any database interaction (P1.1: SQL injection guard).
        if (!string.IsNullOrWhiteSpace(schema))
            SchemaQualifier.ValidateName(schema);

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

        // schemaName is already validated above — safe to pass as a DbUp variable.
        // DbUp performs text substitution into migration scripts; the allowlist validation
        // above ensures no SQL-injectable characters can reach the scripts.
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
        // Validate schema name (P1.1: SQL injection guard).
        if (!string.IsNullOrWhiteSpace(schema))
            SchemaQualifier.ValidateName(schema);

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

    /// <summary>
    /// Checks that the schema in the database was created with a tenancy mode consistent
    /// with <paramref name="singleTenant"/>. Fails fast if they disagree, so a mis-applied
    /// migration set cannot silently run with the wrong stored-function signatures.
    /// </summary>
    /// <remarks>
    /// Detection heuristic: the multi-tenant migration adds a <c>tenant_id</c> column to
    /// <c>alberto_events</c>; the single-tenant migration does not. A mismatch means the DB
    /// was migrated with the opposite mode from what the code expects.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured tenancy mode does not match the schema shape.
    /// </exception>
    public static void ValidateTenancyMode(string connectionString, string? schema, bool singleTenant)
    {
        var tableSchema = string.IsNullOrWhiteSpace(schema) ? "public" : schema;

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = @tableSchema
              AND table_name   = 'alberto_events'
              AND column_name  = 'tenant_id'
            """;
        cmd.Parameters.AddWithValue("tableSchema", tableSchema);

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        var hasTenantColumn = count > 0;

        // Mismatch: code says single-tenant but DB has the multi-tenant column, or vice-versa.
        if (singleTenant && hasTenantColumn)
            throw new InvalidOperationException(
                $"Alberto tenancy mismatch (schema '{tableSchema}'): the application is configured for " +
                "single-tenant mode but the database was migrated in multi-tenant mode " +
                "(alberto_events.tenant_id exists). " +
                "Re-run migrations with the correct mode, or update the application configuration.");

        if (!singleTenant && !hasTenantColumn)
            throw new InvalidOperationException(
                $"Alberto tenancy mismatch (schema '{tableSchema}'): the application is configured for " +
                "multi-tenant mode but the database was migrated in single-tenant mode " +
                "(alberto_events.tenant_id is absent). " +
                "Re-run migrations with the correct mode, or update the application configuration.");
    }

    private static bool IsInFolder(string scriptName, string folderPath)
    {
        // Script names are like "Alberto.Dcb.Postgres.Migrations.001_InitialSchema.sql"
        // or "Alberto.Dcb.Postgres.Migrations.SingleTenant.001_InitialSchema.sql"
        var prefix = $"Alberto.Dcb.Postgres.{folderPath}.";

        if (!scriptName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Exclude subdirectories. A script sitting directly in the folder has exactly one dot left
        // — the one before "sql" — so anything with more is in a nested folder: SingleTenant when
        // filtering for Migrations, Catalog likewise. Matching on the subfolder's name instead
        // would silently admit the next folder someone adds.
        var remainder = scriptName[prefix.Length..];
        return remainder.Count(c => c == '.') == 1;
    }

    private static void EnsureSchemaExists(string connectionString, string schema)
    {
        // schema is already validated by ValidateName before reaching this method.
        // Use a quoted identifier to produce a safe DDL statement even though the
        // allowlist already excludes all problematic characters.
        var quotedSchema = SchemaQualifier.ValidateAndQuote(schema);

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {quotedSchema}";
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
