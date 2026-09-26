using System.Reflection;
using DbUp;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// Applies the tenant shard catalog's schema to the control database.
/// </summary>
/// <remarks>
/// Separate from <see cref="PostgresMigrator"/>, with a journal table of its own, because the
/// catalog is not an event store: it holds one table, it has no tenancy mode, and it may well
/// live in a database that also hosts a shard's schema. Sharing a journal would make each
/// migrator see the other's scripts as pending.
/// </remarks>
public static class PostgresCatalogMigrator
{
    private const string ScriptFolder = "Migrations.Catalog";
    private const string JournalTable = "schemaversions_catalog";

    /// <summary>
    /// Creates the catalog table if it is not there yet.
    /// </summary>
    /// <param name="connectionString">Connection string for the control database.</param>
    /// <param name="schema">Optional schema. Null means the connection's default schema.</param>
    /// <param name="logger">Where migration output goes. Null falls back to the console.</param>
    /// <param name="ensureDatabase">
    /// Whether to connect to the <c>postgres</c> maintenance database and create the control
    /// database if it is missing. Set false for a least-privilege role that can only connect to
    /// its own, already-existing database.
    /// </param>
    public static MigrationResult Migrate(
        string connectionString,
        string? schema = null,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        bool ensureDatabase = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (!string.IsNullOrWhiteSpace(schema))
            SchemaQualifier.ValidateName(schema);

        if (ensureDatabase)
            EnsureDatabase.For.PostgresqlDatabase(connectionString);

        if (!string.IsNullOrWhiteSpace(schema))
            EnsureSchemaExists(connectionString, schema);

        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        var schemaPrefix = string.IsNullOrWhiteSpace(schema) ? "" : $"{schema}.";

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.StartsWith(
                    $"Alberto.Postgres.{ScriptFolder}.", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .LogTo(logger is null
                ? new DbUp.Engine.Output.ConsoleUpgradeLog()
                : new UpgradeLogAdapter(logger))
            .WithVariable("schema", schemaName)
            .WithVariable("schema_prefix", schemaPrefix)
            .JournalToPostgresqlTable(schemaName, JournalTable)
            .Build();

        var result = upgrader.PerformUpgrade();

        return new MigrationResult(
            result.Successful,
            result.Scripts.Select(s => s.Name).ToArray(),
            result.Error);
    }

    private static void EnsureSchemaExists(string connectionString, string schema)
    {
        var quotedSchema = SchemaQualifier.ValidateAndQuote(schema);

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS {quotedSchema}";
        command.ExecuteNonQuery();
    }
}
