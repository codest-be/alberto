using System.Reflection;
using Alberto.Dcb.Postgres;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// Migration upgrade-path and parity tests covering TEST-6 and TEST-8 from the Alberto DCB audit.
/// </summary>
/// <remarks>
/// <para>
/// <b>TEST-6 — Upgrade path</b>: proves that a database already migrated to the pre-009 schema
/// can be upgraded in-place by running <see cref="PostgresMigrator.Migrate"/> and ends up with
/// the same schema shape as a fresh install.  Tests both the multi-tenant and single-tenant
/// migration sets.
/// </para>
/// <para>
/// <b>TEST-8 — Parity</b>: a pure file-inspection guard (no containers required) that
/// ensures the single-tenant and multi-tenant migration sets stay in lockstep — same script
/// numbers and base names for shared scripts, and <c>tenant_id</c> never appearing as SQL
/// content in single-tenant scripts.  This test fails the CI run before a corrupt migration
/// can reach a real database, guarding against the class of silent corruption described in
/// finding P1.4 of the audit.
/// </para>
/// </remarks>
public sealed class MigrationUpgradeAndParityTests
{
    // =========================================================================
    // Constants / reflection helpers (used by both TEST-6 and TEST-8)
    // =========================================================================

    /// <summary>Embedded-resource name prefix for multi-tenant migration scripts.</summary>
    private const string MultiTenantPrefix = "Alberto.Dcb.Postgres.Migrations.";

    /// <summary>Embedded-resource name prefix for single-tenant migration scripts.</summary>
    private const string SingleTenantPrefix = "Alberto.Dcb.Postgres.Migrations.SingleTenant.";

    private static readonly Assembly MigratorAssembly = typeof(PostgresMigrator).Assembly;

    /// <summary>
    /// Returns embedded resource names for the chosen migration set, sorted by name.
    /// The multi-tenant filter excludes the <c>SingleTenant</c> subdirectory so that
    /// both lists are disjoint.
    /// </summary>
    private static List<string> GetMigrationResourceNames(bool singleTenant)
    {
        var prefix = singleTenant ? SingleTenantPrefix : MultiTenantPrefix;
        return MigratorAssembly
            .GetManifestResourceNames()
            .Where(n =>
            {
                if (!n.StartsWith(prefix, StringComparison.Ordinal)) return false;

                // Multi-tenant prefix is a substring of the single-tenant prefix.
                // Exclude single-tenant names when listing multi-tenant names.
                if (!singleTenant && n.StartsWith(SingleTenantPrefix, StringComparison.Ordinal))
                    return false;

                return true;
            })
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>
    /// Extracts the 3-digit leading script number from an embedded resource name.
    /// Returns <see langword="null"/> when the name does not follow the expected pattern.
    /// </summary>
    private static int? ParseScriptNumber(string resourceName, string prefix)
    {
        if (resourceName.Length <= prefix.Length) return null;
        var rest = resourceName[prefix.Length..]; // e.g. "001_InitialSchema.sql"
        return rest.Length >= 3 && int.TryParse(rest[..3], out var n) ? n : null;
    }

    /// <summary>
    /// Returns the base file name of a migration resource (the part after the namespace prefix).
    /// For example: <c>"001_InitialSchema.sql"</c>.
    /// </summary>
    private static string GetBaseName(string resourceName, string prefix)
        => resourceName[prefix.Length..];

    // =========================================================================
    // TEST-6  Upgrade path  (Testcontainers required)
    // =========================================================================

    /// <summary>
    /// Simulates a real-world upgrade: applies scripts 001-008 directly via Npgsql,
    /// records them in the DbUp journal so that the migrator treats them as already-applied,
    /// then calls <see cref="PostgresMigrator.Migrate"/> which executes only the remaining
    /// scripts (009+).  Asserts both that the migration succeeds and that the resulting schema
    /// shape matches the expected post-migration state.
    /// </summary>
    /// <remarks>
    /// Each upgrade test spins up its own isolated container so the two variants do not
    /// interfere with each other. These cannot move onto the shared
    /// <see cref="Infrastructure.PostgresCluster"/>: they exercise the migrator itself from a
    /// pre-009 baseline, so they need a database that has never been migrated, which is
    /// precisely what the cluster's templates are not.
    /// </remarks>
    [Fact]
    public async Task MultiTenant_UpgradesFromPre009Schema_ToCurrentMigrations_Successfully()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();

        // Phase 1: apply scripts 001-008 directly + record in DbUp journal.
        await ApplyScriptsAndPopulateJournalAsync(connectionString, singleTenant: false, upToScriptNumber: 8);

        // Phase 2: run the full migrator — should only apply 009+ since 001-008 are journaled.
        var result = PostgresMigrator.Migrate(connectionString, singleTenant: false);
        result.Successful.Should().BeTrue(because: result.Error?.Message ?? "migration failed");

        // The migrator should have run scripts 009 through the current highest number.
        result.ExecutedScripts.Should().NotBeEmpty(because: "scripts 009+ must have been applied");
        result.ExecutedScripts.Should().OnlyContain(
            s => ParseScriptNumber(s, MultiTenantPrefix) >= 9,
            because: "only scripts numbered 009 or later should have been executed in phase 2");

        // Assert expected schema shape after the full migration.
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // 009: alberto_read_by_types_and_tags is a new function introduced in script 009.
        (await FunctionExistsAsync(conn, "alberto_read_by_types_and_tags"))
            .Should().BeTrue(because: "function added by migration 009 must exist after upgrade");

        // 010: the notify trigger must be a STATEMENT-level trigger (not row-level).
        var triggerOrientation = await GetTriggerActionOrientationAsync(conn, "alberto_trg_notify_events");
        triggerOrientation.Should().Be("STATEMENT",
            because: "migration 010 replaces the FOR EACH ROW trigger with a FOR EACH STATEMENT trigger");

        // 011: checkpoint table must have fillfactor=70.
        (await TableHasFillfactorAsync(conn, "alberto_processor_checkpoints", fillfactor: 70))
            .Should().BeTrue(because: "migration 011 sets fillfactor=70 on the checkpoint table");

        // 012 (multi-tenant only): alberto_tenants catalog table must exist.
        (await TableExistsAsync(conn, "alberto_tenants"))
            .Should().BeTrue(because: "migration 012 creates the alberto_tenants catalog table");

        // 016: existing outbox tables must gain the complete leased-claim fence.
        await AssertOutboxClaimLeaseColumnsExistAsync(conn);

        // Core invariant: alberto_events must still have the tenant_id column.
        (await ColumnExistsAsync(conn, "alberto_events", "tenant_id"))
            .Should().BeTrue(because: "multi-tenant schema must retain the tenant_id column on alberto_events");
    }

    [Fact]
    public async Task SingleTenant_UpgradesFromPre009Schema_ToCurrentMigrations_Successfully()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();

        // Phase 1: apply scripts 001-008 directly + record in DbUp journal.
        await ApplyScriptsAndPopulateJournalAsync(connectionString, singleTenant: true, upToScriptNumber: 8);

        // Phase 2: run the full migrator — should only apply 009+ since 001-008 are journaled.
        var result = PostgresMigrator.Migrate(connectionString, singleTenant: true);
        result.Successful.Should().BeTrue(because: result.Error?.Message ?? "migration failed");

        result.ExecutedScripts.Should().NotBeEmpty(because: "scripts 009+ must have been applied");
        result.ExecutedScripts.Should().OnlyContain(
            s => ParseScriptNumber(s, SingleTenantPrefix) >= 9,
            because: "only scripts numbered 009 or later should have been executed in phase 2");

        // Assert expected schema shape after the full migration.
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // 009: alberto_read_by_types_and_tags is a new function introduced in script 009.
        (await FunctionExistsAsync(conn, "alberto_read_by_types_and_tags"))
            .Should().BeTrue(because: "function added by migration 009 must exist after upgrade");

        // 010: the notify trigger must be a STATEMENT-level trigger.
        var triggerOrientation = await GetTriggerActionOrientationAsync(conn, "alberto_trg_notify_events");
        triggerOrientation.Should().Be("STATEMENT",
            because: "migration 010 replaces the FOR EACH ROW trigger with a FOR EACH STATEMENT trigger");

        // 011: checkpoint table must have fillfactor=70.
        (await TableHasFillfactorAsync(conn, "alberto_processor_checkpoints", fillfactor: 70))
            .Should().BeTrue(because: "migration 011 sets fillfactor=70 on the checkpoint table");

        // 016: existing outbox tables must gain the complete leased-claim fence.
        await AssertOutboxClaimLeaseColumnsExistAsync(conn);

        // Core invariant: single-tenant schema must NOT have a tenant_id column on events.
        (await ColumnExistsAsync(conn, "alberto_events", "tenant_id"))
            .Should().BeFalse(because: "single-tenant schema must not have a tenant_id column on alberto_events");
    }

    // =========================================================================
    // TEST-8  Parity  (pure file inspection — no containers required)
    // =========================================================================

    /// <summary>
    /// Every script in the single-tenant set must have a corresponding script in the
    /// multi-tenant set with the same base file name.  This ensures that bug-fixes and
    /// performance improvements applied to one variant are also applied to the other.
    /// </summary>
    [Fact]
    public void SingleTenantScripts_HaveMatchingMultiTenantScripts_ByBaseName()
    {
        var stNames = GetMigrationResourceNames(singleTenant: true)
            .Select(n => GetBaseName(n, SingleTenantPrefix))
            .ToHashSet();

        var mtNames = GetMigrationResourceNames(singleTenant: false)
            .Select(n => GetBaseName(n, MultiTenantPrefix))
            .ToHashSet();

        // Every single-tenant script must exist in the multi-tenant set.
        var missingInMultiTenant = stNames.Except(mtNames).ToList();
        missingInMultiTenant.Should().BeEmpty(
            because: "every single-tenant migration must have a corresponding multi-tenant migration " +
                     "— add the missing script(s) to the multi-tenant Migrations folder");

        // Single-tenant scripts that appear only in multi-tenant set (like 012) are acceptable
        // (tenant-specific additions), but any scripts that exist ONLY in single-tenant are a bug.
        // (This branch is implicitly covered by the assertion above.)
    }

    /// <summary>
    /// For each shared script number, the base file names in both sets must be identical.
    /// A mismatch (e.g., single-tenant has "009_SomeFix.sql" while multi-tenant has
    /// "009_DifferentFix.sql") indicates the sets have drifted in content.
    /// </summary>
    [Fact]
    public void SharedScriptNumbers_HaveIdenticalBaseNames_InBothVariants()
    {
        var stByNumber = GetMigrationResourceNames(singleTenant: true)
            .Select(n => (Number: ParseScriptNumber(n, SingleTenantPrefix)!.Value, BaseName: GetBaseName(n, SingleTenantPrefix)))
            .ToDictionary(x => x.Number, x => x.BaseName);

        var mtByNumber = GetMigrationResourceNames(singleTenant: false)
            .Select(n => (Number: ParseScriptNumber(n, MultiTenantPrefix)!.Value, BaseName: GetBaseName(n, MultiTenantPrefix)))
            .ToDictionary(x => x.Number, x => x.BaseName);

        // For every script number that appears in the single-tenant set,
        // the multi-tenant set must have the same base name at that number.
        var drifted = stByNumber
            .Where(kv => mtByNumber.TryGetValue(kv.Key, out var mtName) && mtName != kv.Value)
            .Select(kv => $"script {kv.Key:D3}: ST='{kv.Value}' vs MT='{mtByNumber[kv.Key]}'")
            .ToList();

        drifted.Should().BeEmpty(
            because: "shared script numbers must have the same file name in both variants — " +
                     "rename the diverging script(s) so they match");
    }

    /// <summary>
    /// Single-tenant migration scripts must not reference <c>tenant_id</c> as SQL
    /// content (only comments are permitted to mention it, e.g. "no tenant_id columns").
    /// Presence of <c>tenant_id</c> in non-comment SQL lines indicates that a multi-tenant
    /// script was accidentally copied into the single-tenant folder without removing the
    /// tenant column — exactly the silent corruption described in finding P1.4.
    /// </summary>
    [Fact]
    public void SingleTenantScripts_DoNotContain_TenantId_InSqlContent()
    {
        var violations = new List<string>();

        foreach (var resourceName in GetMigrationResourceNames(singleTenant: true))
        {
            using var stream = MigratorAssembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd()
                .Split('\n')
                .Select(l => l.TrimEnd('\r'));

            var sqlLines = lines
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))
                .ToList();

            var offending = sqlLines
                .Where(l => l.Contains("tenant_id", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Trim())
                .ToList();

            if (offending.Count > 0)
            {
                var baseName = GetBaseName(resourceName, SingleTenantPrefix);
                violations.AddRange(offending.Select(l => $"{baseName}: {l}"));
            }
        }

        violations.Should().BeEmpty(
            because: "single-tenant migration scripts must not reference tenant_id in SQL — " +
                     "remove or replace the offending lines or move the script to the multi-tenant set");
    }

    /// <summary>
    /// Multi-tenant migration scripts (001-009) that define event-read functions
    /// (<c>alberto_read_*</c>) must include a <c>p_tenant_id</c> parameter.  A read
    /// function in this range that omits <c>p_tenant_id</c> would silently return
    /// cross-tenant data — the class of bug described in P1.4 of the audit.
    /// </summary>
    /// <remarks>
    /// Structural scripts (e.g. checkpoint upsert fixes, column additions) that do not
    /// define read functions are intentionally excluded: those scripts operate at a
    /// cross-tenant level by design and do not need a tenant filter parameter.
    /// </remarks>
    [Fact]
    public void MultiTenantReadFunctions_001Through009_Include_TenantIdParameter()
    {
        var violations = new List<string>();

        var coreScripts = GetMigrationResourceNames(singleTenant: false)
            .Where(n => ParseScriptNumber(n, MultiTenantPrefix) is int num && num <= 9);

        foreach (var resourceName in coreScripts)
        {
            using var stream = MigratorAssembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            // Strip comment lines so that comments don't influence the check.
            var sqlContent = string.Join('\n', content
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

            // Only audit scripts that define event-read functions.
            // Structural scripts (checkpoint upsert, column additions, triggers) are excluded
            // because they operate at a cross-tenant level and do not need a tenant filter.
            if (!sqlContent.Contains("alberto_read_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!sqlContent.Contains("p_tenant_id", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(GetBaseName(resourceName, MultiTenantPrefix));
            }
        }

        violations.Should().BeEmpty(
            because: "multi-tenant event-read functions in scripts 001-009 must include a p_tenant_id " +
                     "parameter — a read function missing this parameter would silently return cross-tenant data");
    }

    // =========================================================================
    // Private helpers for upgrade-path tests
    // =========================================================================

    /// <summary>
    /// Applies SQL scripts <c>001</c> through <paramref name="upToScriptNumber"/> from the
    /// specified migration set directly via Npgsql (bypassing DbUp), then records each script
    /// in the DbUp journal table.  A subsequent call to <see cref="PostgresMigrator.Migrate"/>
    /// will see these scripts as already-applied and execute only the higher-numbered ones.
    /// </summary>
    /// <remarks>
    /// DbUp substitution variables are replaced before execution:
    /// <c>$schema_prefix$</c> → empty string (public schema),
    /// <c>$schema$</c> → <c>public</c>.
    /// Each script runs in its own transaction, mirroring <c>WithTransactionPerScript</c>.
    /// </remarks>
    private static async Task ApplyScriptsAndPopulateJournalAsync(
        string connectionString,
        bool singleTenant,
        int upToScriptNumber)
    {
        var prefix = singleTenant ? SingleTenantPrefix : MultiTenantPrefix;

        var scriptsToApply = GetMigrationResourceNames(singleTenant)
            .Where(n => ParseScriptNumber(n, prefix) is int num && num <= upToScriptNumber)
            .ToList();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // Create the DbUp PostgreSQL journal table.  PostgresMigrator.Migrate will query
        // this table to determine which scripts have already been applied.
        await using (var createJournal = conn.CreateCommand())
        {
            createJournal.CommandText = """
                CREATE TABLE IF NOT EXISTS public.schemaversions (
                    schemaversionsid SERIAL NOT NULL,
                    scriptname       VARCHAR(255) NOT NULL,
                    applied          TIMESTAMPTZ NOT NULL,
                    CONSTRAINT pk_schemaversions_id PRIMARY KEY (schemaversionsid)
                )
                """;
            await createJournal.ExecuteNonQueryAsync();
        }

        foreach (var resourceName in scriptsToApply)
        {
            // Load the embedded SQL and substitute DbUp variables for the default (public) schema.
            using var stream = MigratorAssembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var sql = (await reader.ReadToEndAsync())
                .Replace("$schema_prefix$", "")
                .Replace("$schema$", "public");

            // Execute the migration script in its own transaction, then record it in
            // the journal — both in the same transaction so a failure keeps the journal clean.
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                await using (var migrationCmd = conn.CreateCommand())
                {
                    migrationCmd.Transaction = tx;
                    migrationCmd.CommandText = sql;
                    await migrationCmd.ExecuteNonQueryAsync();
                }

                // The journal scriptname is the full embedded resource name, which is what
                // DbUp stores and later queries to determine pending scripts.
                await using (var journalCmd = conn.CreateCommand())
                {
                    journalCmd.Transaction = tx;
                    journalCmd.CommandText = """
                        INSERT INTO public.schemaversions (scriptname, applied)
                        VALUES (@name, now())
                        """;
                    journalCmd.Parameters.AddWithValue("@name", resourceName);
                    await journalCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
    }

    // =====================================================================
    // Schema-shape query helpers
    // =====================================================================

    private static async Task<bool> FunctionExistsAsync(NpgsqlConnection conn, string functionName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pg_proc WHERE proname = @name";
        cmd.Parameters.AddWithValue("@name", functionName);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    /// <summary>
    /// Returns the <c>action_orientation</c> of the named trigger on <c>alberto_events</c>
    /// (<c>"ROW"</c> or <c>"STATEMENT"</c>), or <see langword="null"/> if no such trigger exists.
    /// </summary>
    private static async Task<string?> GetTriggerActionOrientationAsync(NpgsqlConnection conn, string triggerName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT action_orientation
            FROM information_schema.triggers
            WHERE trigger_name = @name
              AND event_object_schema = 'public'
              AND event_object_table = 'alberto_events'
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@name", triggerName);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result as string;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the table's <c>reloptions</c> contains
    /// the specified <c>fillfactor</c> value.
    /// </summary>
    private static async Task<bool> TableHasFillfactorAsync(NpgsqlConnection conn, string tableName, int fillfactor)
    {
        await using var cmd = conn.CreateCommand();
        // reloptions is a text[] in pg_class; casting to text produces a comma-separated list.
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_class c
                INNER JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = @name
                  AND n.nspname = 'public'
                  AND c.relkind = 'r'
                  AND array_to_string(c.reloptions, ',') LIKE @pattern
            )
            """;
        cmd.Parameters.AddWithValue("@name", tableName);
        cmd.Parameters.AddWithValue("@pattern", $"%fillfactor={fillfactor}%");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name = @name
            """;
        cmd.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task AssertOutboxClaimLeaseColumnsExistAsync(NpgsqlConnection conn)
    {
        foreach (var column in new[] { "claim_id", "claimed_by", "claim_expires_at" })
        {
            (await ColumnExistsAsync(conn, "alberto_outbox_entries", column))
                .Should().BeTrue(
                    because: $"migration 016 adds the outbox claim fence column {column}");
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlConnection conn, string tableName, string columnName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name   = @table
              AND column_name  = @col
            """;
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@col", columnName);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }
}
