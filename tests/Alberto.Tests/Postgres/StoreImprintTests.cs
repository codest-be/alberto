using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
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

        flip.Should().Throw<AlbertoStoreMismatchException>(
                because: "the two migration sets share a journal, so running the wrong one leaves the " +
                         "store in a state neither set describes")
            .Which.Message.Should().Contain("ALB0021");

        (await CountJournalEntriesAsync(connectionString)).Should().Be(journalledBefore,
            because: "the gate runs ahead of the first script, so a refused flip must leave the store untouched");
    }

    [Fact]
    public async Task The_refusal_names_the_store_the_declaration_and_the_remedy()
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: true);

        var flip = () => PostgresMigrator.Migrate(connectionString, singleTenant: false);

        var message = flip.Should().Throw<AlbertoStoreMismatchException>().Which.Message;

        message.Should().Contain("created single-tenant",
            because: "an operator has to be told what the store already is");
        message.Should().Contain("declares .WithTenancy()",
            because: "and which half of the disagreement is theirs to change");
        message.Should().Contain($"Recorded in {StoreImprintTableName}",
            because: "the imprint, not the sniff, is what spoke here");
        message.Should().Contain("No migration scripts were run.",
            because: "the operator needs to know the store is unchanged before deciding what to do");
    }

    [Fact]
    public async Task A_mismatched_store_is_refused_by_inspection_too()
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: true);

        var inspect = () => PostgresMigrator.GetPendingMigrations(connectionString, singleTenant: false);

        inspect.Should().Throw<AlbertoStoreMismatchException>(
            because: "listing every script of a set the store can never accept would report a " +
                     "mismatched store as merely behind");
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

        flip.Should().Throw<AlbertoStoreMismatchException>(
                because: "every store that existed before the imprint has none, and so does one left " +
                         "behind by a migration that failed partway")
            .Which.Message.Should().Contain("Inferred from alberto_events",
                because: "the attribution has to say which source disagreed with the declaration");
    }

    [Fact]
    public async Task A_store_with_no_imprint_is_backfilled_when_the_mode_matches()
    {
        var connectionString = await FreshDatabaseAsync();
        Migrated(connectionString, singleTenant: true);
        await DropImprintAsync(connectionString);

        Migrated(connectionString, singleTenant: true);

        (await ReadTenancyImprintAsync(connectionString)).Should().Be("single",
            because: "a rolling upgrade must record what the store already is without an operator step");
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

        (await ReadTenancyImprintAsync(connectionString))
            .Should().Be(singleTenant ? "single" : "multi");
    }

    [Fact]
    public async Task ValidateTenancyMode_is_quiet_on_a_store_that_has_no_mode_yet()
    {
        var connectionString = await FreshDatabaseAsync();

        var validate = () => PostgresMigrator.ValidateTenancyMode(
            connectionString, schema: null, singleTenant: false);

        validate.Should().NotThrow(
            because: "a database with no events table is fresh rather than mismatched; before the gate " +
                     "moved ahead of the migration this same call threw for multi-tenant");
    }

    [Fact]
    public async Task Migrating_twice_records_exactly_one_imprint()
    {
        var connectionString = await FreshDatabaseAsync();

        Migrated(connectionString, singleTenant: true);
        Migrated(connectionString, singleTenant: true);

        (await CountImprintRowsAsync(connectionString)).Should().Be(1,
            because: "the imprint is written ON CONFLICT DO NOTHING, so a restart never rewrites it");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>The imprint table's name, duplicated because the constant itself is internal.</summary>
    private const string StoreImprintTableName = "alberto_store_imprint";

    private static void Migrated(string connectionString, bool singleTenant)
    {
        var result = PostgresMigrator.Migrate(connectionString, singleTenant: singleTenant);
        result.Successful.Should().BeTrue(because: result.Error?.Message ?? "migration failed");
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
