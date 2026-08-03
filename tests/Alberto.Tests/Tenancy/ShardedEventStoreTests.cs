using Alberto.Subscriptions;
using Alberto.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Tenancy;

/// <summary>
/// Sharding against two real databases: a tenant's events are in its own database and nowhere
/// else, and everything shard-local — positions, checkpoints, dead letters — stays that way.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShardedEventStoreTests(ShardedPostgresFixture fixture)
    : IClassFixture<ShardedPostgresFixture>
{
    private const string ModuleKey = ShardedPostgresFixture.ModuleKey;

    private const string Concept = "shardorder";

    /// <summary>A concept id nothing else in the suite will collide with.</summary>
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static EventToPersist Event(string id) => new()
    {
        EventType = new EventType("shard-test-event"),
        Tags = [EventTag.Parse($"{Concept}:{id}")],
        EventData = """{"test": true}""",
    };

    private AsyncServiceScope ScopeFor(string tenantId)
    {
        var scope = fixture.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        return scope;
    }

    private static IEventStore StoreOf(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);

    private static async Task<long> CountEventsAsync(NpgsqlDataSource dataSource, string tenantId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM alberto_events WHERE tenant_id = @tenant");
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task A_tenants_events_are_written_to_its_own_database_and_to_no_other()
    {
        await using (var scope = ScopeFor(ShardedPostgresFixture.TenantOnDb1))
            await StoreOf(scope).AppendAsync([Event(NewId())], cancellationToken: TestContext.Current.CancellationToken);

        (await CountEventsAsync(fixture.Db1, ShardedPostgresFixture.TenantOnDb1)).Should().BeGreaterThan(0);
        (await CountEventsAsync(fixture.Db2, ShardedPostgresFixture.TenantOnDb1)).Should().Be(0);
    }

    [Fact]
    public async Task Two_tenants_on_two_shards_never_see_each_other()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = NewId();
        var query = DcbQuery.For(Concept, id);

        await using (var scope = ScopeFor(ShardedPostgresFixture.TenantOnDb1))
            await StoreOf(scope).AppendAsync([Event(id)], cancellationToken: ct);

        await using (var scope = ScopeFor(ShardedPostgresFixture.TenantOnDb2))
        {
            // Same tag, other database: not merely filtered out by tenant_id — it is not there.
            (await StoreOf(scope).StreamAsync(query, cancellationToken: ct)).Should().BeEmpty();

            await StoreOf(scope).AppendAsync([Event(id)], cancellationToken: ct);
            (await StoreOf(scope).StreamAsync(query, cancellationToken: ct)).Should().ContainSingle();
        }

        await using (var scope = ScopeFor(ShardedPostgresFixture.TenantOnDb1))
            (await StoreOf(scope).StreamAsync(query, cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Row_level_tenancy_still_separates_two_tenants_inside_one_shard()
    {
        // Sharding sits above row-level tenancy rather than replacing it: two tenants that share
        // db1 are isolated exactly as they would be in an unsharded module.
        var ct = TestContext.Current.CancellationToken;
        var id = NewId();
        var query = DcbQuery.For(Concept, id);

        await using (var scope = ScopeFor(ShardedPostgresFixture.TenantOnDb1))
            await StoreOf(scope).AppendAsync([Event(id)], cancellationToken: ct);

        await using (var scope = ScopeFor(ShardedPostgresFixture.TenantAlsoOnDb1))
            (await StoreOf(scope).StreamAsync(query, cancellationToken: ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_shard_has_its_own_position_sequence()
    {
        // Positions come from a per-database BIGSERIAL, so they are only comparable within one
        // shard. Two shards therefore hand out overlapping numbers, which is exactly why
        // StreamAllAsync is not allowed to union across them.
        var ct = TestContext.Current.CancellationToken;

        await using var db1 = ScopeFor(ShardedPostgresFixture.TenantOnDb1);
        await using var db2 = ScopeFor(ShardedPostgresFixture.TenantOnDb2);

        var beforeOnDb2 = await StoreOf(db2).GetLastPositionAsync(ct);

        for (var i = 0; i < 3; i++)
            await StoreOf(db1).AppendAsync([Event(NewId())], cancellationToken: ct);

        (await StoreOf(db2).GetLastPositionAsync(ct)).Should().Be(beforeOnDb2);
    }

    [Fact]
    public async Task Checkpoints_are_shard_local()
    {
        var ct = TestContext.Current.CancellationToken;
        const string processorId = "shard-checkpoint-test";

        var db1 = fixture.Services.GetRequiredKeyedService<ICheckpointStore>($"{ModuleKey}#db1");
        var db2 = fixture.Services.GetRequiredKeyedService<ICheckpointStore>($"{ModuleKey}#db2");

        await db1.SaveAsync(processorId, 42, ct);

        (await db1.GetAsync(processorId, ct)).Should().Be(42);
        (await db2.GetAsync(processorId, ct)).Should().BeNull();
    }

    [Fact]
    public async Task Dead_letters_are_shard_local()
    {
        var ct = TestContext.Current.CancellationToken;
        const string processorId = "shard-deadletter-test";

        var db1 = fixture.Services.GetRequiredKeyedService<IDeadLetterStore>($"{ModuleKey}#db1");
        var db2 = fixture.Services.GetRequiredKeyedService<IDeadLetterStore>($"{ModuleKey}#db2");

        await db1.StoreAsync(
            new DeadLetterEntry
            {
                Id = Guid.NewGuid(),
                ProcessorId = processorId,
                EventId = Guid.NewGuid(),
                EventType = "shard-test-event",
                EventData = "{}",
                ErrorMessage = "boom",
                StackTrace = null,
                AttemptCount = 1,
                FailedAt = DateTimeOffset.UtcNow,
                GlobalPosition = 1,
            },
            ct);

        (await db1.CountAsync(processorId, ct)).Should().Be(1);
        (await db2.CountAsync(processorId, ct)).Should().Be(0);
    }

    [Fact]
    public async Task The_catalog_holds_shard_ids_and_no_connection_details()
    {
        // The whole point of storing only the id: a dump of this table leaks no credentials, and
        // a shard can move host without a data migration.
        var ct = TestContext.Current.CancellationToken;

        await using var command = fixture.Catalog.CreateCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'alberto_tenant_shards'");
        await using var reader = await command.ExecuteReaderAsync(ct);

        var columns = new List<string>();
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));

        columns.Should().BeEquivalentTo("module_key", "tenant_id", "shard_id", "assigned_at");
    }

    [Fact]
    public async Task An_assignment_survives_a_new_process()
    {
        // A second host reading the same catalog must place the tenant in the same database; the
        // resolver's cache is an optimisation, not the record.
        var map = fixture.Services.GetRequiredKeyedService<ITenantShardMap>(ModuleKey);

        var fresh = new TenantShardResolver(
            ModuleKey, map, new HashSet<string>(["db1", "db2"], StringComparer.Ordinal), null);

        (await fresh.ResolveAsync(ShardedPostgresFixture.TenantOnDb2, TestContext.Current.CancellationToken))
            .Should().Be("db2");
    }

    [Fact]
    public async Task The_first_assignment_of_a_tenant_wins_and_a_later_one_learns_it()
    {
        // Writing a tenant's events to two databases is unrecoverable, so a second AssignAsync
        // reports the assignment already in force rather than moving the tenant.
        var ct = TestContext.Current.CancellationToken;
        var map = fixture.Services.GetRequiredKeyedService<ITenantShardMap>(ModuleKey);
        var tenantId = $"race{Guid.NewGuid():N}"[..20];

        (await map.AssignAsync(tenantId, "db1", ct)).Should().Be("db1");
        (await map.AssignAsync(tenantId, "db2", ct)).Should().Be("db1");
        (await map.ResolveAsync(tenantId, ct)).Should().Be("db1");
    }

    [Fact]
    public async Task Concurrent_assignments_of_one_tenant_agree_on_one_shard()
    {
        var ct = TestContext.Current.CancellationToken;
        var map = fixture.Services.GetRequiredKeyedService<ITenantShardMap>(ModuleKey);
        var tenantId = $"race{Guid.NewGuid():N}"[..20];

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            await map.AssignAsync(tenantId, i % 2 == 0 ? "db1" : "db2", ct)));

        results.Should().AllBe(results[0]);
        (await map.ResolveAsync(tenantId, ct)).Should().Be(results[0]);
    }

    [Fact]
    public async Task A_tenant_the_catalog_does_not_know_is_refused_rather_than_guessed()
    {
        // This module declares no default shard, so an unmapped tenant must not be placed
        // anywhere — the wrong database is not recoverable once events are in it.
        await using var scope = ScopeFor("unmappedtenant");

        var append = async () => await StoreOf(scope).AppendAsync(
            [Event(NewId())], cancellationToken: TestContext.Current.CancellationToken);

        await append.Should().ThrowAsync<UnknownTenantException>();
    }
}
