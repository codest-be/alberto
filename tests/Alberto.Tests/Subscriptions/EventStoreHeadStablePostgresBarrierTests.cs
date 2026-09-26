using System.Text.Json;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Proves the premise behind the head-tracker stall alarm: any open write transaction on the
/// same Postgres server — including one against a different database — pins
/// <c>pg_snapshot_xmin</c> and freezes <see cref="IEventStoreHeadBackend.GetStableHeadAsync"/>
/// below a committed event, until that transaction ends.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StableHeadBarrierCollection.Name)]
public sealed class EventStoreHeadStablePostgresBarrierTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    [EventType("barrier-probe")]
    private sealed record Probe(int N) : IEvent;

    private static IEventToPersist Event(int n) => new EventToPersist
    {
        EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(Probe))),
        Tags = [],
        EventData = JsonSerializer.Serialize(new Probe(n)),
    };

    /// <summary>
    /// An open transaction on the SAME database that has taken an xid pins the barrier: the
    /// stable head does not reach a since-committed event until that transaction ends.
    /// </summary>
    [Fact]
    public async Task OpenTransaction_SameDatabase_PinsStableHeadUntilItEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PostgresEventStoreBackend(fixture.DataSource);
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(ct);

        // Force an xid on a separate, still-open connection/transaction — no writes needed,
        // pg_current_xact_id() alone allocates one and pins it as the oldest in-flight xid.
        await using var pinning = new NpgsqlConnection(fixture.ConnectionString);
        await pinning.OpenAsync(ct);
        await using var pinningTx = await pinning.BeginTransactionAsync(ct);
        await using (var xidCmd = new NpgsqlCommand("SELECT pg_current_xact_id()", pinning, pinningTx))
            await xidCmd.ExecuteScalarAsync(ct);

        // Now commit an event on the real connection, after the pinning xid was allocated.
        await backend.AppendAsync([Event(1)], cancellationToken: ct);
        var newPosition = await backend.GetLastPositionAsync(ct);
        Assert.True(newPosition > startPosition);

        var stalledHead = await headBackend.GetStableHeadAsync(startPosition, ct);
        Assert.True(stalledHead < newPosition,
            $"expected the barrier to hold the stable head below {newPosition} while the pinning " +
            $"transaction is open, got {stalledHead}");

        // End the pinning transaction — the barrier must release.
        await pinningTx.CommitAsync(ct);

        var releasedHead = await headBackend.GetStableHeadAsync(startPosition, ct);
        Assert.True(releasedHead >= newPosition,
            $"expected the barrier to release to >= {newPosition} after commit, got {releasedHead}");
    }

    /// <summary>
    /// The same pin, this time via a rollback instead of a commit — the barrier must release
    /// either way, since <c>pg_current_snapshot</c> only cares that the transaction has ended.
    /// </summary>
    [Fact]
    public async Task OpenTransaction_RolledBack_ReleasesStableHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PostgresEventStoreBackend(fixture.DataSource);
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(ct);

        await using var pinning = new NpgsqlConnection(fixture.ConnectionString);
        await pinning.OpenAsync(ct);
        await using var pinningTx = await pinning.BeginTransactionAsync(ct);
        await using (var xidCmd = new NpgsqlCommand("SELECT pg_current_xact_id()", pinning, pinningTx))
            await xidCmd.ExecuteScalarAsync(ct);

        await backend.AppendAsync([Event(2)], cancellationToken: ct);
        var newPosition = await backend.GetLastPositionAsync(ct);

        var stalledHead = await headBackend.GetStableHeadAsync(startPosition, ct);
        Assert.True(stalledHead < newPosition);

        await pinningTx.RollbackAsync(ct);

        var releasedHead = await headBackend.GetStableHeadAsync(startPosition, ct);
        Assert.True(releasedHead >= newPosition);
    }

    /// <summary>
    /// A read-only session that only holds a snapshot — never allocates an xid — does NOT pin
    /// the barrier. This is what justifies narrowing the operator query (and the warning's
    /// remedy) to <c>backend_xid IS NOT NULL</c> rather than <c>backend_xmin IS NOT NULL</c>,
    /// which would also match the operator's own diagnostic query.
    /// </summary>
    [Fact]
    public async Task OpenTransaction_ReadOnly_NoXid_DoesNotPinStableHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PostgresEventStoreBackend(fixture.DataSource);
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(ct);

        await using var readOnly = new NpgsqlConnection(fixture.ConnectionString);
        await readOnly.OpenAsync(ct);
        await using var readOnlyTx = await readOnly.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct);
        // No pg_current_xact_id() call here — a plain SELECT never allocates an xid.
        await using (var probe = new NpgsqlCommand("SELECT 1", readOnly, readOnlyTx))
            await probe.ExecuteScalarAsync(ct);

        await backend.AppendAsync([Event(4)], cancellationToken: ct);
        var newPosition = await backend.GetLastPositionAsync(ct);

        var head = await headBackend.GetStableHeadAsync(startPosition, ct);
        Assert.True(head >= newPosition,
            $"expected a read-only session with no xid to NOT pin the barrier, got {head} < {newPosition}");

        await readOnlyTx.CommitAsync(ct);
    }

    /// <summary>
    /// The cross-database variant: an open write transaction on a SECOND database in the SAME
    /// Postgres server (same postmaster, same shared <c>pg_current_snapshot()</c>) still pins
    /// the barrier for the module's database, because <c>pg_snapshot_xmin</c> is server-wide,
    /// not per-database. This decides how wide the docs describe the blast radius: "any writer
    /// anywhere on the server", not "any writer on this database".
    /// </summary>
    [Fact]
    public async Task OpenTransaction_OtherDatabase_SameServer_AlsoPinsStableHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PostgresEventStoreBackend(fixture.DataSource);
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(ct);

        // A second, unrelated database on the same server/postmaster as the fixture's.
        var otherDb = "barrier_cross_db_" + Guid.NewGuid().ToString("N")[..8];
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var originalDb = adminBuilder.Database;
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"""CREATE DATABASE "{otherDb}";""", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            adminBuilder.Database = otherDb;
            await using var pinning = new NpgsqlConnection(adminBuilder.ConnectionString);
            await pinning.OpenAsync(ct);
            await using var pinningTx = await pinning.BeginTransactionAsync(ct);
            await using (var xidCmd = new NpgsqlCommand("SELECT pg_current_xact_id()", pinning, pinningTx))
                await xidCmd.ExecuteScalarAsync(ct);

            await backend.AppendAsync([Event(3)], cancellationToken: ct);
            var newPosition = await backend.GetLastPositionAsync(ct);

            var stalledHead = await headBackend.GetStableHeadAsync(startPosition, ct);
            Assert.True(stalledHead < newPosition,
                $"expected a transaction on database '{otherDb}' (same server) to still pin " +
                $"the stable head on '{originalDb}' below {newPosition}, got {stalledHead}");

            await pinningTx.CommitAsync(ct);

            var releasedHead = await headBackend.GetStableHeadAsync(startPosition, ct);
            Assert.True(releasedHead >= newPosition);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync(ct);
            await using var drop = new NpgsqlCommand(
                $"""DROP DATABASE IF EXISTS "{otherDb}" WITH (FORCE);""", admin);
            await drop.ExecuteNonQueryAsync(ct);
        }
    }
}

// The premise these tests prove is also what breaks them under the parallel suite: snapshot
// xmin is server-wide, and every Postgres-backed test shares one server, so any other test's
// open write transaction pins the stable head and fails a "released" assertion. Run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StableHeadBarrierCollection
{
    public const string Name = "stable-head-barrier";
}
