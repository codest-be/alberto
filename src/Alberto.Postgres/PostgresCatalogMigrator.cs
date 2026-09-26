using System.Reflection;
using DbUp;
using Microsoft.Extensions.Logging;
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
    /// <remarks>
    /// Writes no output. At startup the hosted service passes its <see cref="ILogger"/> and the
    /// catalog's <see cref="PostgresOptions.EnsureDatabase"/> through the internal overload.
    /// </remarks>
    public static MigrationResult Migrate(string connectionString, string? schema = null)
        => Migrate(connectionString, new MigrationOptions { Schema = schema });

    /// <summary>
    /// Creates the catalog table if it is not there yet.
    /// </summary>
    /// <param name="connectionString">Connection string for the control database.</param>
    /// <param name="options">
    /// The schema, logger and whether to create the database first.
    /// <see cref="MigrationOptions.SingleTenant"/> is ignored: the catalog has no tenancy mode.
    /// </param>
    /// <remarks>
    /// Internal because a public <c>Migrate(string, MigrationOptions)</c> would make the existing
    /// <c>Migrate(connectionString, null)</c> call ambiguous for consumers (RS0027).
    /// </remarks>
    internal static MigrationResult Migrate(string connectionString, MigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var schema = options.Schema;

        if (!string.IsNullOrWhiteSpace(schema))
            SchemaQualifier.ValidateName(schema);

        var upgradeLog = PostgresMigrator.CreateUpgradeLog(options.Logger);

        if (options.EnsureDatabase)
            EnsureDatabase.For.PostgresqlDatabase(connectionString, upgradeLog);

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
            .LogTo(upgradeLog)
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
