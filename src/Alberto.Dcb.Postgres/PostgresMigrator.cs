using System.Reflection;
using DbUp;
using DbUp.Engine;
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

        // Before a single script runs: the store decides whether this migration set may be
        // applied to it at all. Running the wrong set is not a no-op that a later correction
        // undoes — the two sets share a journal, so a half-applied one leaves the store in a
        // state neither set describes.
        GuardTenancyMode(connectionString, schema, singleTenant);

        // A handful of statements — CREATE INDEX CONCURRENTLY above all — are rejected by
        // PostgreSQL inside a transaction block, and those are exactly the statements that make
        // a migration safe to run against a live database. DbUp's transaction mode is a property
        // of the upgrader, not of the script, so a script that needs one of them is run by its
        // own upgrader built WithoutTransaction. Scripts are grouped into consecutive runs of
        // the same mode so that ordering is preserved and the common case is still a single
        // upgrader over every script.
        var scriptFolder = singleTenant ? SingleTenantScriptFolder : MultiTenantScriptFolder;
        var runs = PartitionIntoTransactionRuns(GetOrderedScriptNames(scriptFolder));

        var executed = new List<string>();

        foreach (var run in runs)
        {
            var upgrader = BuildUpgradeEngine(
                connectionString,
                schema,
                singleTenant,
                scriptFilter: run.Contains,
                runsOutsideTransaction: run.RunsOutsideTransaction);

            var result = upgrader.PerformUpgrade();
            executed.AddRange(result.Scripts.Select(s => s.Name));

            // Stop at the first failing run rather than pressing on: a later script may well
            // depend on the schema the failed one was supposed to produce.
            if (!result.Successful)
                return new MigrationResult(false, executed, result.Error);
        }

        // Recorded only once every script has landed. A run that fails partway leaves no imprint,
        // but it does leave an events table of the mode it was migrating to — which the sniff in
        // ResolveTenancy reads, so the next attempt is still held to that mode. Recording before
        // the scripts would instead pin a store that was never successfully created.
        StoreImprint.RecordTenancy(connectionString, ResolveSchemaName(schema), singleTenant);

        return new MigrationResult(true, executed, null);
    }

    /// <summary>
    /// Marker a migration script places in a leading comment to declare that it must not run
    /// inside a transaction block.
    /// </summary>
    /// <remarks>
    /// Kept as a comment rather than a file-name convention so the reason travels with the
    /// statement that needs it, and so renaming a script cannot silently change how it runs.
    /// </remarks>
    internal const string NoTransactionMarker = "alberto:no-transaction";

    /// <summary>
    /// A consecutive group of migration scripts that share a transaction mode.
    /// </summary>
    private sealed record ScriptRun(bool RunsOutsideTransaction, HashSet<string> Names)
    {
        public bool Contains(string scriptName) => Names.Contains(scriptName);
    }

    /// <summary>
    /// Returns the embedded script names for a migration folder in the order DbUp would run
    /// them. DbUp orders by script name, and the scripts are named with a zero-padded numeric
    /// prefix, so an ordinal sort is that order.
    /// </summary>
    private static List<string> GetOrderedScriptNames(string scriptFolder)
        => Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(n => IsInFolder(n, scriptFolder))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Splits the ordered scripts into maximal consecutive runs that share a transaction mode.
    /// With no non-transactional scripts this yields exactly one run, which is the pre-existing
    /// behaviour.
    /// </summary>
    private static List<ScriptRun> PartitionIntoTransactionRuns(List<string> orderedScripts)
    {
        var runs = new List<ScriptRun>();

        foreach (var scriptName in orderedScripts)
        {
            var outsideTransaction = DeclaresNoTransaction(scriptName);

            if (runs.Count == 0 || runs[^1].RunsOutsideTransaction != outsideTransaction)
                runs.Add(new ScriptRun(outsideTransaction, new HashSet<string>(StringComparer.Ordinal)));

            runs[^1].Names.Add(scriptName);
        }

        return runs;
    }

    /// <summary>
    /// Reads an embedded script and reports whether it carries <see cref="NoTransactionMarker"/>.
    /// </summary>
    /// <remarks>
    /// The marker must be the entire content of a comment line. Matching it anywhere in the file
    /// would let a script that merely explains the marker in its header — as several of them do —
    /// silently opt itself out of transactional execution.
    /// </remarks>
    private static bool DeclaresNoTransaction(string scriptName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(scriptName);
        if (stream is null) return false;

        using var reader = new StreamReader(stream);
        return ScriptDeclaresNoTransaction(reader.ReadToEnd());
    }

    /// <summary>
    /// Reports whether the given migration script text declares that it must run outside a
    /// transaction block, by carrying <see cref="NoTransactionMarker"/> as the whole content
    /// of a comment line.
    /// </summary>
    internal static bool ScriptDeclaresNoTransaction(string scriptText)
    {
        foreach (var line in scriptText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("--", StringComparison.Ordinal)) continue;

            if (trimmed[2..].Trim().Equals(NoTransactionMarker, StringComparison.Ordinal))
                return true;
        }

        return false;
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

        // Held to the same gate as Migrate. Without it, inspection would list every script of a
        // set the store can never accept, and report a mismatched store as merely behind.
        GuardTenancyMode(connectionString, schema, singleTenant);

        var upgrader = BuildUpgradeEngine(connectionString, schema, singleTenant);

        return upgrader.GetScriptsToExecute()
            .Select(s => s.Name)
            .ToArray();
    }

    /// <summary>
    /// Checks that the store was created with a tenancy mode consistent with
    /// <paramref name="singleTenant"/>, and throws if they disagree.
    /// </summary>
    /// <remarks>
    /// <see cref="Migrate"/> already runs this gate before any script, so calling it separately
    /// is a second opinion rather than the load-bearing check. It remains public for callers that
    /// migrate out of band and want the check on its own.
    /// <para>
    /// A store with no <c>alberto_events</c> table and no imprint is fresh, not mismatched: it is
    /// free to become either mode, so this returns quietly. Before the gate moved ahead of the
    /// migration, the same call against an empty database threw for multi-tenant.
    /// </para>
    /// </remarks>
    /// <exception cref="AlbertoStoreMismatchException">
    /// Thrown when the store's tenancy mode does not match <paramref name="singleTenant"/>.
    /// </exception>
    public static void ValidateTenancyMode(string connectionString, string? schema, bool singleTenant)
    {
        if (!string.IsNullOrWhiteSpace(schema))
            SchemaQualifier.ValidateName(schema);

        GuardTenancyMode(connectionString, schema, singleTenant);
    }

    /// <summary>
    /// Refuses a migration set the store cannot accept. Does nothing for a fresh store, which has
    /// no mode yet.
    /// </summary>
    private static void GuardTenancyMode(string connectionString, string? schema, bool singleTenant)
    {
        var schemaName = ResolveSchemaName(schema);

        if (StoreImprint.ResolveTenancy(connectionString, schemaName) is { } recorded
            && recorded.SingleTenant != singleTenant)
        {
            throw AlbertoStoreMismatchException.ForTenancy(schemaName, recorded, singleTenant);
        }
    }

    /// <summary>
    /// The schema as it appears in catalog queries: the default, unqualified schema is
    /// <c>public</c>.
    /// </summary>
    private static string ResolveSchemaName(string? schema)
        => string.IsNullOrWhiteSpace(schema) ? "public" : schema;

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

    /// <summary>
    /// Builds the engine that runs — or, for inspection, merely lists — migration scripts.
    /// <paramref name="scriptFilter"/> defaults to every script in the tenancy's folder, which is
    /// what inspection wants; <see cref="Migrate"/> narrows it to a single transaction run and
    /// sets <paramref name="runsOutsideTransaction"/> from what that run declared.
    /// </summary>
    private static UpgradeEngine BuildUpgradeEngine(
        string connectionString,
        string? schema,
        bool singleTenant,
        Func<string, bool>? scriptFilter = null,
        bool runsOutsideTransaction = false)
    {
        var schemaName = ResolveSchemaName(schema);
        var schemaPrefix = string.IsNullOrWhiteSpace(schema) ? "" : $"{schema}.";
        var scriptFolder = singleTenant ? SingleTenantScriptFolder : MultiTenantScriptFolder;
        scriptFilter ??= scriptName => IsInFolder(scriptName, scriptFolder);

        // Both execution and inspection must share the same script selection, variables,
        // transaction policy, and schema-local journal. Otherwise "pending" can disagree
        // with the migration that will actually run. The transaction mode is the one thing
        // that legitimately varies, and only per run: inspection never executes anything.
        var builder = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                scriptFilter)
            .LogToConsole()
            .WithVariable("schema", schemaName)
            .WithVariable("schema_prefix", schemaPrefix)
            .JournalToPostgresqlTable(schemaName, "schemaversions");

        builder = runsOutsideTransaction
            ? builder.WithoutTransaction()
            : builder.WithTransactionPerScript();

        return builder.Build();
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
