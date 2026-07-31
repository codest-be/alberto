using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Guards the advisory-lock fix that prevents two pods from racing on a fresh database.
/// </summary>
/// <remarks>
/// Before the lock was added, both migrators passed <c>GuardTenancyMode</c> (no imprint
/// yet, no <c>alberto_events</c> yet), both reached DbUp with an empty journal, and both
/// attempted to insert into <c>schemaversions</c>. The loser's transaction rolled back,
/// taking all its DDL with it and crashing the pod. With the session-level advisory lock,
/// exactly one migrator runs at a time; the second acquires the lock after the first has
/// already recorded every script, finds the journal fully populated, and completes without
/// running anything.
/// </remarks>
public sealed class ConcurrentMigrationTests(PostgresCluster cluster)
{
    private Task<string> FreshDatabaseAsync(CancellationToken ct = default) =>
        cluster.CloneAsync(PostgresTemplates.Empty, nameof(ConcurrentMigrationTests), ct);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_migrators_racing_against_a_fresh_database_both_complete_without_error(bool singleTenant)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await FreshDatabaseAsync(ct);

        // Race two concurrent migrators at the same starting line.
        var t1 = Task.Run(() => PostgresMigrator.Migrate(connectionString, singleTenant: singleTenant), ct);
        var t2 = Task.Run(() => PostgresMigrator.Migrate(connectionString, singleTenant: singleTenant), ct);

        var results = await Task.WhenAll(t1, t2);

        results[0].Successful.Should().BeTrue(
            because: results[0].Error?.Message ?? "first migrator failed");
        results[1].Successful.Should().BeTrue(
            because: results[1].Error?.Message ?? "second migrator failed");

        // Every script must have been applied exactly once. GetPendingMigrations returns
        // nothing when the journal is complete and consistent.
        PostgresMigrator.GetPendingMigrations(connectionString, singleTenant: singleTenant)
            .Should().BeEmpty(
                because: "all migration scripts must have been applied exactly once across the two runs");

        // The two runs together must have executed each script once. One run wins the lock
        // and runs all N scripts; the other acquires the lock afterward, finds all N scripts
        // already journalled, and executes nothing. Their executed-script counts therefore
        // sum to N — the same as the journal entry count.
        var journalCount = await CountJournalEntriesAsync(connectionString, ct);
        var totalExecuted = results[0].ExecutedScripts.Count + results[1].ExecutedScripts.Count;
        ((long)totalExecuted).Should().Be(journalCount,
            because: "between them the two runs execute every script exactly once — " +
                     "no duplicates and no gaps");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static async Task<long> CountJournalEntriesAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM schemaversions", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
