using System.Text.Json;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// The head must never pass a position whose append is still open, whatever order the open
/// append and the appends committed above it took their transaction ids in. Multi-tenant stores
/// lock per tenant, so appends of different tenants draw positions from the one sequence
/// concurrently: an open append at P can sit below committed appends at P+1 and P+2. If the head
/// skipped P as a permanent gap, every subscriber would checkpoint past it and never see the event.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StableHeadBarrierCollection.Name)]
public sealed class EventStoreHeadInFlightGapPostgresTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    [Theory]
    [InlineData(true)]  // the append committed above the open one took its xid first
    [InlineData(false)] // the open append took its xid first
    public async Task Head_stays_below_an_open_append_until_it_commits(bool committedTookXidFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PostgresEventStoreBackend(fixture.DataSource);
        var head = new EventStoreHead(backend, TimeSpan.FromMinutes(1));
        await head.RefreshAsync(ct);
        await head.RefreshAsync(ct);

        await using var committed = await Gap.Begin(fixture.ConnectionString, ct);
        await using var open = await Gap.Begin(fixture.ConnectionString, ct);
        if (committedTookXidFirst) { await Gap.Xid(committed, ct); await Gap.Xid(open, ct); }
        else { await Gap.Xid(open, ct); await Gap.Xid(committed, ct); }

        // The open append draws P; the other appends draw P+1 and P+2 and commit.
        var p = (long)(await Gap.Scalar(open, "SELECT nextval('alberto_events_global_position_seq')", ct))!;
        await Gap.Scalar(committed, "INSERT INTO alberto_events (event_type) VALUES ('gap-probe') RETURNING global_position", ct);
        await committed.Tx.CommitAsync(ct);
        await backend.AppendAsync([Gap.Event(1)], cancellationToken: ct);

        await head.RefreshAsync(ct);
        await head.RefreshAsync(ct);
        Assert.True(head.Current < p, $"head {head.Current} passed the open append at {p}");

        await Gap.Scalar(open, $"INSERT INTO alberto_events (global_position, event_type) VALUES ({p}, 'gap-probe') RETURNING global_position", ct);
        await open.Tx.CommitAsync(ct);
        await head.RefreshAsync(ct);
        Assert.True(head.Current >= p + 2, $"head {head.Current} did not reach {p + 2} once the append committed");
    }

    [Fact]
    public async Task Head_passes_the_gap_a_rolled_back_append_leaves_without_waiting_the_refresh_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PostgresEventStoreBackend(fixture.DataSource);
        var head = new EventStoreHead(backend, TimeSpan.FromMinutes(1));

        await using var open = await Gap.Begin(fixture.ConnectionString, ct);
        await Gap.Xid(open, ct);
        var p = (long)(await Gap.Scalar(open, "SELECT nextval('alberto_events_global_position_seq')", ct))!;
        await backend.AppendAsync([Gap.Event(2)], cancellationToken: ct);
        await backend.AppendAsync([Gap.Event(3)], cancellationToken: ct);
        await head.StartAsync(ct);
        try
        {
            Assert.True(head.Current < p);

            // No NOTIFY follows a rollback, and the interval is a minute: only the head's own quick
            // look-again can pass the gap.
            await open.Tx.RollbackAsync(ct);
            await Gap.Until(() => head.Current == p + 2, TimeSpan.FromSeconds(5), ct);
        }
        finally { await head.StopAsync(ct); }
    }

    [Fact]
    public async Task A_head_starting_cold_passes_every_old_gap_in_its_first_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PostgresEventStoreBackend(fixture.DataSource);
        for (var i = 0; i < 10; i++)
        {
            await using var rolledBack = await Gap.Begin(fixture.ConnectionString, ct);
            await Gap.Scalar(rolledBack, "INSERT INTO alberto_events (event_type) VALUES ('gap-probe') RETURNING global_position", ct);
            await rolledBack.Tx.RollbackAsync(ct);
            await backend.AppendAsync([Gap.Event(i)], cancellationToken: ct);
        }
        var last = await backend.GetLastPositionAsync(ct);

        // Windows of two positions: each one after the first starts on a gap.
        var head = new EventStoreHead(backend, TimeSpan.FromMinutes(1), windowSize: 2);
        await head.RefreshAsync(ct);
        Assert.Equal(last, head.Current);
    }
}

/// <summary>
/// The same, with the real multi-tenant append: tenant A's append is held open after drawing its
/// position (a lock on its tenant row, which the append's trigger updates once the row's
/// last_seen_at is stale), while tenant B's
/// appends commit above it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StableHeadBarrierCollection.Name)]
public sealed class EventStoreHeadInFlightGapMultiTenantTests(MultiTenantDbFixture fixture)
    : IClassFixture<MultiTenantDbFixture>
{
    [Fact]
    public async Task Head_stays_below_an_open_tenant_append_until_it_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new PostgresTenantEventStoreBackend(fixture.DataSource);
        var head = new EventStoreHead(new TenantEventStoreDecorator(store, new TenantAccessor(new TenantContext())), TimeSpan.FromMinutes(1));
        await store.AppendForTenant("gap-a", [Gap.Event(0)], cancellationToken: ct);
        await store.AppendForTenant("gap-b", [Gap.Event(0)], cancellationToken: ct);
        await head.RefreshAsync(ct);

        // The trigger touches a known tenant's row only once last_seen_at is a minute old, so age
        // it first: otherwise the append never waits on the lock below.
        await Gap.Query(fixture.DataSource, "UPDATE alberto_tenants SET last_seen_at = now() - INTERVAL '1 hour' WHERE tenant_id = 'gap-a'", ct);
        await using var rowLock = await Gap.Begin(fixture.ConnectionString, ct);
        await Gap.Scalar(rowLock, "SELECT 1 FROM alberto_tenants WHERE tenant_id = 'gap-a' FOR UPDATE", ct);
        var parked = store.AppendForTenant("gap-a", [Gap.Event(1)], cancellationToken: ct);
        await Gap.Until(async () => (long)(await Gap.Query(fixture.DataSource,
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", ct))! == 1,
            TimeSpan.FromSeconds(5), ct);
        var p = (long)(await Gap.Query(fixture.DataSource, "SELECT last_value FROM alberto_events_global_position_seq", ct))!;

        await store.AppendForTenant("gap-b", [Gap.Event(2)], cancellationToken: ct);
        await store.AppendForTenant("gap-b", [Gap.Event(3)], cancellationToken: ct);
        await head.RefreshAsync(ct);
        await head.RefreshAsync(ct);
        Assert.True(head.Current < p, $"head {head.Current} passed tenant A's open append at {p}");

        await rowLock.Tx.CommitAsync(ct);
        Assert.Equal(p, Assert.Single(await parked).GlobalPosition);
        await head.RefreshAsync(ct);
        Assert.Equal(p + 2, head.Current);
    }
}

internal static class Gap
{
    [EventType("gap-probe")]
    private sealed record Probe(int N) : IEvent;

    public static IEventToPersist Event(int n) => new EventToPersist
    {
        EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(Probe))),
        Tags = [],
        EventData = JsonSerializer.Serialize(new Probe(n)),
    };

    public sealed record Session(NpgsqlConnection Connection, NpgsqlTransaction Tx) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Tx.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    public static async Task<Session> Begin(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return new Session(connection, await connection.BeginTransactionAsync(ct));
    }

    public static Task Xid(Session s, CancellationToken ct) => Scalar(s, "SELECT pg_current_xact_id()", ct);

    public static async Task<object?> Scalar(Session s, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, s.Connection, s.Tx);
        return await cmd.ExecuteScalarAsync(ct);
    }

    public static async Task<object?> Query(NpgsqlDataSource dataSource, string sql, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync(ct);
    }

    public static Task Until(Func<bool> condition, TimeSpan timeout, CancellationToken ct) =>
        Until(() => Task.FromResult(condition()), timeout, ct);

    public static async Task Until(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"not within {timeout}");
            await Task.Delay(10, ct);
        }
    }
}
