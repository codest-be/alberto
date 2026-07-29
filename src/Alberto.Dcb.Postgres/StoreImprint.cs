using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>How a store's recorded tenancy was established.</summary>
internal enum TenancySource
{
    /// <summary>Read from the imprint table.</summary>
    Imprint,

    /// <summary>Inferred from the shape of <c>alberto_events</c>, for a store predating the imprint.</summary>
    SchemaShape,
}

/// <summary>What a store is, and how that was established.</summary>
internal readonly record struct RecordedTenancy(bool SingleTenant, TenancySource Source);

/// <summary>
/// The store's own record of what it was created as, checked before any migration script runs.
/// </summary>
/// <remarks>
/// <para>
/// The table is created by this class rather than by a migration script, the same way DbUp
/// manages its own journal out-of-band. It has to be: the check that must precede every script
/// cannot depend on a script having run.
/// </para>
/// <para>
/// The schema shape is the source of truth and the imprint is a durable record of it. That is
/// why <see cref="ResolveTenancy"/> falls back to sniffing <c>alberto_events</c> — it covers
/// stores that predate the imprint, and equally a store left half-migrated, which has the tables
/// of the mode it was migrated in but no imprint row because the run never completed.
/// </para>
/// <para>
/// Only tenancy is recorded today. The table is keyed by fact name so that a module key or a
/// shard id — neither of which can be sniffed at all — becomes an insert rather than a migration
/// of the imprint table itself.
/// </para>
/// </remarks>
internal static class StoreImprint
{
    /// <summary>The imprint table, unqualified.</summary>
    internal const string TableName = "alberto_store_imprint";

    private const string TenancyFact = "tenancy";
    private const string SingleTenantValue = "single";
    private const string MultiTenantValue = "multi";

    /// <summary>PostgreSQL <c>duplicate_table</c>, raised when two replicas create the table at once.</summary>
    private const string DuplicateTable = "42P07";

    /// <summary>PostgreSQL <c>unique_violation</c>, which the same race can surface from <c>pg_type</c>.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Returns what the store at <paramref name="schemaName"/> was created as, or null when it
    /// is fresh and free to become either.
    /// </summary>
    internal static RecordedTenancy? ResolveTenancy(string connectionString, string schemaName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        EnsureTable(connection, schemaName);

        if (ReadTenancyFact(connection, schemaName) is { } recorded)
            return new RecordedTenancy(recorded, TenancySource.Imprint);

        // No imprint. A store with an events table predates the imprint, or was migrated by a
        // run that failed before it could record one; either way its own columns say what it is.
        // A store without one has nothing to contradict.
        return SniffEventsTable(connection, schemaName) is { } sniffed
            ? new RecordedTenancy(sniffed, TenancySource.SchemaShape)
            : null;
    }

    /// <summary>
    /// Records the tenancy of a store that has just been migrated successfully. Never overwrites
    /// an existing value: the imprint changes only when an operator deliberately changes it.
    /// </summary>
    internal static void RecordTenancy(string connectionString, string schemaName, bool singleTenant)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        EnsureTable(connection, schemaName);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Qualify(schemaName)} (fact, value)
            VALUES (@fact, @value)
            ON CONFLICT (fact) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("fact", TenancyFact);
        cmd.Parameters.AddWithValue("value", singleTenant ? SingleTenantValue : MultiTenantValue);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the imprint table if it is absent.
    /// </summary>
    /// <remarks>
    /// Every replica runs this as the first thing it does at startup, which is a hotter race than
    /// <c>CREATE SCHEMA IF NOT EXISTS</c> elsewhere in the migrator. PostgreSQL's IF NOT EXISTS is
    /// not atomic against a concurrent creator, so the two SQLSTATEs that race produces are
    /// swallowed — the other replica created exactly the table this one wanted.
    /// </remarks>
    private static void EnsureTable(NpgsqlConnection connection, string schemaName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {Qualify(schemaName)} (
                fact        VARCHAR(100) PRIMARY KEY,
                value       VARCHAR(200) NOT NULL,
                recorded_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;

        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState is DuplicateTable or UniqueViolation)
        {
            // Lost the race; the table is there.
        }
    }

    /// <summary>
    /// Returns the recorded tenancy, or null when the fact has never been written.
    /// </summary>
    private static bool? ReadTenancyFact(NpgsqlConnection connection, string schemaName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT value FROM {Qualify(schemaName)} WHERE fact = @fact";
        cmd.Parameters.AddWithValue("fact", TenancyFact);

        return (cmd.ExecuteScalar() as string) switch
        {
            SingleTenantValue => true,
            MultiTenantValue => false,
            _ => null,
        };
    }

    /// <summary>
    /// Returns what the events table says the store is, or null when there is no events table.
    /// </summary>
    /// <remarks>
    /// The multi-tenant migration puts a <c>tenant_id</c> column on <c>alberto_events</c> and the
    /// single-tenant one does not, so the column's presence is the distinction. Absence of the
    /// table itself is not single-tenant — it is no answer at all, which is why this returns null
    /// rather than true.
    /// </remarks>
    private static bool? SniffEventsTable(NpgsqlConnection connection, string schemaName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FILTER (WHERE column_name = 'tenant_id'),
                   COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = @tableSchema
              AND table_name   = 'alberto_events'
            """;
        cmd.Parameters.AddWithValue("tableSchema", schemaName);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var tenantColumns = reader.GetInt64(0);
        var totalColumns = reader.GetInt64(1);

        if (totalColumns == 0)
            return null;

        return tenantColumns == 0;
    }

    private static string Qualify(string schemaName) => new SchemaQualifier(schemaName).Table(TableName);
}
