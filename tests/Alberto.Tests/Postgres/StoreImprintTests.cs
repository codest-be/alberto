using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Pins the gate that refuses a migration set the store cannot accept.
/// </summary>
/// <remarks>
/// Single-tenant and multi-tenant are two disjoint script sets that share one journal, so a
/// module that flips <c>.WithTenancy()</c> on an existing store used to have the wrong set run
/// against it before anything checked. These tests hold the gate ahead of the first script, and
/// hold the store untouched when it fires.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class StoreImprintTests(PostgresCluster cluster)
{
    private Task<string> FreshDatabaseAsync() =>
        cluster.CloneAsync(PostgresTemplates.Empty, nameof(StoreImprintTests));

    // =========================================================================
    // The gate
    // =========================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Flipping_the_tenancy_of_an_existing_store_is_refused(bool createdSingleTenant)
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: createdSingleTenant);

        var journalledBefore = await CountJournalEntriesAsync(connectionString);

        var flip = () => PostgresMigrator.Migrate(connectionString, singleTenant: !createdSingleTenant);

        // the two migration sets share a journal, so running the wrong one leaves the store in a state
        // neither set describes
        var exception = Assert.ThrowsAny<AlbertoStoreMismatchException>(flip);
        Assert.Contains("ALB0021", exception.Message);

        // the gate runs ahead of the first script, so a refused flip must leave the store untouched
        Assert.Equal(journalledBefore, await CountJournalEntriesAsync(connectionString));
    }

    [Fact]
    public async Task The_refusal_names_the_store_the_declaration_and_the_remedy()
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: true);

        var flip = () => PostgresMigrator.Migrate(connectionString, singleTenant: false);

        var exception = Assert.ThrowsAny<AlbertoStoreMismatchException>(flip);
        var message = exception.Message;

        // an operator has to be told what the store already is
        Assert.Contains("created single-tenant", message);
        // and which half of the disagreement is theirs to change
        Assert.Contains("declares .WithTenancy()", message);
        // the imprint, not the sniff, is what spoke here
        Assert.Contains($"Recorded in {StoreImprintTableName}", message);
        // the operator needs to know the store is unchanged before deciding what to do
        Assert.Contains("No migration scripts were run.", message);
    }

    [Fact]
    public async Task A_mismatched_store_is_refused_by_inspection_too()
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: true);

        var inspect = () => PostgresMigrator.GetPendingMigrations(connectionString, singleTenant: false);

        // listing every script of a set the store can never accept would report a mismatched store as
        // merely behind
        Assert.ThrowsAny<AlbertoStoreMismatchException>(inspect);
    }

    // =========================================================================
    // Stores that predate the imprint
    // =========================================================================

    [Fact]
    public async Task A_store_with_no_imprint_is_still_held_to_its_schema_shape()
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: true);
        await DropImprintAsync(connectionString);

        var flip = () => PostgresMigrator.Migrate(connectionString, singleTenant: false);

        // every store that existed before the imprint has none, and so does one left behind by a
        // migration that failed partway
        var exception = Assert.ThrowsAny<AlbertoStoreMismatchException>(flip);
        // the attribution has to say which source disagreed with the declaration
        Assert.Contains("Inferred from alberto_events", exception.Message);
    }

    [Fact]
    public async Task A_store_with_no_imprint_is_backfilled_when_the_mode_matches()
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: true);
        await DropImprintAsync(connectionString);

        Migrated(connectionString, singleTenant: true);

        // a rolling upgrade must record what the store already is without an operator step
        Assert.Equal("single", await ReadTenancyImprintAsync(connectionString));
    }

    // =========================================================================
    // Stores with no mode yet
    // =========================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fresh_database_may_become_either_mode(bool singleTenant)
    {
        var connectionString = await FreshDatabaseAsync();

        Migrated(connectionString, singleTenant);

        Assert.Equal(singleTenant ? "single" : "multi", await ReadTenancyImprintAsync(connectionString));
    }

    [Fact]
    public async Task ValidateTenancyMode_is_quiet_on_a_store_that_has_no_mode_yet()
    {
        var connectionString = await FreshDatabaseAsync();

        var validate = () => PostgresMigrator.ValidateTenancyMode(
            connectionString, schema: null, singleTenant: false);

        // a database with no events table is fresh rather than mismatched; before the gate moved ahead of
        // the migration this same call threw for multi-tenant
        validate();
    }

    [Fact]
    public async Task Migrating_twice_records_exactly_one_imprint()
    {
        var connectionString = await FreshDatabaseAsync();

        Migrated(connectionString, singleTenant: true);
        Migrated(connectionString, singleTenant: true);

        // the imprint is written ON CONFLICT DO NOTHING, so a restart never rewrites it
        Assert.Equal(1, await CountImprintRowsAsync(connectionString));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>The imprint table's name, duplicated because the constant itself is internal.</summary>
    private const string StoreImprintTableName = "alberto_store_imprint";

    private static void Migrated(string connectionString, bool singleTenant)
    {
        var result = PostgresMigrator.Migrate(connectionString, singleTenant: singleTenant);
        Assert.True(result.Successful, result.Error?.Message ?? "migration failed");
    }

    private static async Task DropImprintAsync(string connectionString) =>
        await ExecuteAsync(connectionString, $"DROP TABLE {StoreImprintTableName}");

    private static async Task<long> CountJournalEntriesAsync(string connectionString) =>
        Convert.ToInt64(await ScalarAsync(connectionString, "SELECT COUNT(*) FROM schemaversions"));

    private static async Task<long> CountImprintRowsAsync(string connectionString) =>
        Convert.ToInt64(await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {StoreImprintTableName}"));

    private static async Task<string?> ReadTenancyImprintAsync(string connectionString) =>
        await ScalarAsync(
            connectionString,
            $"SELECT value FROM {StoreImprintTableName} WHERE fact = 'tenancy'") as string;

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
