using Alberto.Dcb.Postgres;
using Alberto.Dcb.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// A single-tenant database for the semantic assertions. They share one happily — nothing they
/// assert on is database-wide.
/// </summary>
public sealed class PrefixBoundarySemanticsFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// Pins how a wildcard tag boundary (<c>order:*</c>) reaches the tag table during append.
/// </summary>
/// <remarks>
/// The DCB conflict check runs inside the append transaction, so its cost is append latency.
/// Matching wildcard tags with <c>tag LIKE prefix || '%'</c> cannot use an index — the pattern
/// is a value from the unnested prefix array rather than a constant, so PostgreSQL has nothing
/// to derive a range from and scans the whole tag table. The scan is worst on the *successful*
/// append, where there is no conflicting row to stop early on, and it grows with the total
/// number of tags ever written.
/// <para>
/// Every test here takes a database of its own rather than sharing a class fixture. The evidence
/// is <c>pg_stat_user_tables</c>, which is per database and not per session: two tests measuring
/// at once would reset each other's counters and read each other's scans.
/// </para>
/// </remarks>
public sealed class AppendPrefixBoundaryAccessPathTests(PostgresCluster cluster)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Tenant = "tenant-a";

    /// <summary>
    /// Concepts that share a leading substring, so an access path that confuses them is visible
    /// here rather than only in the semantic tests.
    /// </summary>
    private static readonly string[] Concepts =
        ["pathorder", "pathorder-archive", "pathorders", "pathcustomer"];

    /// <summary>
    /// The append that finds no conflict — the ordinary, successful one — must reach the tag
    /// table through an index. A sequential scan here is unbounded work inside the append
    /// transaction: it re-reads every tag ever written, on every append.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WildcardBoundaryAppend_ReachesTheTagTableThroughAnIndex(bool union)
    {
        await using var dataSource = await PrivateDatabaseAsync(PostgresTemplates.SingleTenant);
        var backend = new PostgresEventStoreBackend(dataSource);

        for (var batch = 0; batch < 20; batch++)
        {
            var events = Enumerable.Range(0, 50)
                .Select(i => Event("seeded", $"{Concepts[i % Concepts.Length]}:{batch}-{i}"))
                .ToArray();
            await backend.AppendAsync(events, cancellationToken: Ct);
        }

        await AnalyzeAsync(dataSource);

        var head = await backend.GetLastPositionAsync(Ct);
        var query = DcbQuery.Empty.WithTagPrefix("pathorder");
        if (union)
            query = query.AsUnion();

        await ResetStatisticsAsync(dataSource);

        // Boundary anchored at the head, so nothing can conflict: the check has to look at
        // every candidate before concluding there is none.
        await backend.AppendAsync([Event("order-placed", "pathorder:new")], query, head, Ct);

        var (sequentialScans, indexScans) = await TagTableScansAsync(dataSource);

        indexScans.Should().BeGreaterThan(0,
            because: "the wildcard boundary should be resolved through an index");
        sequentialScans.Should().Be(0,
            because: "scanning every tag ever written is unbounded work inside the append transaction");
    }

    /// <summary>
    /// The same guarantee for the tenant-scoped append functions. The multi-tenant migration set
    /// carries its own definitions of them, so fixing one tree says nothing about the other.
    /// These call the functions directly rather than through a backend: the tenant-scoped backend
    /// is internal and needs the tenancy decorator chain around it, and what is under test is the
    /// SQL.
    /// </summary>
    [Theory]
    [InlineData("alberto_append_events_v2")] // wildcard boundary, union composition
    [InlineData("alberto_append_events_v5")] // wildcard boundary, intersect composition
    public async Task TenantScopedWildcardBoundaryAppend_ReachesTheTagTableThroughAnIndex(
        string appendFunction)
    {
        await using var dataSource = await PrivateDatabaseAsync(PostgresTemplates.MultiTenant);
        await SeedTenantScopedAsync(dataSource);

        await using var conn = await dataSource.OpenConnectionAsync(Ct);

        var head = (long)(await new NpgsqlCommand(
            "SELECT coalesce(max(global_position), 0) FROM alberto_events", conn)
            .ExecuteScalarAsync(Ct))!;

        await new NpgsqlCommand("SELECT pg_stat_reset()", conn).ExecuteNonQueryAsync(Ct);

        await using var append = new NpgsqlCommand(
            $"""
            SELECT count(*) FROM {appendFunction}(
                @tenant,
                @events::jsonb,
                NULL,
                NULL,
                ARRAY['mtorder:']::varchar(500)[],
                @head)
            """,
            conn);
        append.Parameters.AddWithValue("tenant", Tenant);
        append.Parameters.AddWithValue("events", EventPayload);
        append.Parameters.AddWithValue("head", head);
        await append.ExecuteScalarAsync(Ct);

        var (sequential, indexed) = await TagTableScansAsync(dataSource);

        indexed.Should().BeGreaterThan(0,
            because: "the wildcard boundary should be resolved through an index");
        sequential.Should().Be(0,
            because: "the tenant-scoped conflict check has the same unbounded-scan problem");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private const string EventPayload =
        """
        [{"event_type":"order-placed","event_tags":["mtorder:new"],"event_data":{},"event_metadata":{}}]
        """;

    private static IEventToPersist Event(string eventType, params string[] tags) =>
        new EventToPersist
        {
            EventType = new EventType(eventType),
            EventData = """{"test": true}""",
            Tags = tags.Select(EventTag.Parse).ToArray(),
            Metadata = new Dictionary<string, string>(),
        };

    private async Task<NpgsqlDataSource> PrivateDatabaseAsync(PostgresTemplate template) =>
        NpgsqlDataSource.Create(await cluster.CloneAsync(template, "prefixboundary", Ct));

    /// <summary>
    /// Seeds tag rows across concepts that share a leading substring, straight through SQL —
    /// the access path under test does not depend on how the rows got there.
    /// </summary>
    private static async Task SeedTenantScopedAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync(Ct);
        await using var seed = new NpgsqlCommand(
            """
            INSERT INTO alberto_events
                (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
            SELECT @tenant, gen_random_uuid(), 'seeded', ARRAY[]::varchar(500)[],
                   '{}'::jsonb, '{}'::jsonb, now()
            FROM generate_series(1, 1000);

            INSERT INTO alberto_event_tag_positions (tenant_id, tag, global_position)
            SELECT @tenant,
                   (ARRAY['mtorder', 'mtorder-archive', 'mtorders', 'mtcustomer'])
                       [1 + (e.global_position % 4)] || ':' || e.global_position::text,
                   e.global_position
            FROM alberto_events e
            WHERE e.tenant_id = @tenant;

            ANALYZE alberto_event_tag_positions;
            """,
            conn);
        seed.Parameters.AddWithValue("tenant", Tenant);
        await seed.ExecuteNonQueryAsync(Ct);
    }

    private static async Task AnalyzeAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("ANALYZE alberto_event_tag_positions", conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task ResetStatisticsAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT pg_stat_reset()", conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// Reads how the tag table was reached since the last reset.
    /// </summary>
    /// <remarks>
    /// A backend flushes its statistics when it goes idle, which is not synchronous with the
    /// append returning, so this waits for the boundary check to be accounted for at all before
    /// reading the split. Waiting on the total rather than on either counter keeps the wait
    /// independent of which access path was taken — otherwise "no sequential scan" would be
    /// indistinguishable from "not flushed yet".
    /// </remarks>
    private static async Task<(long SequentialScans, long IndexScans)> TagTableScansAsync(
        NpgsqlDataSource dataSource)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            await using var conn = await dataSource.OpenConnectionAsync(Ct);
            await using var cmd = new NpgsqlCommand(
                """
                SELECT coalesce(seq_scan, 0), coalesce(idx_scan, 0)
                FROM pg_stat_user_tables
                WHERE relname = 'alberto_event_tag_positions'
                """,
                conn);

            await using var reader = await cmd.ExecuteReaderAsync(Ct);
            if (await reader.ReadAsync(Ct))
            {
                var sequential = reader.GetInt64(0);
                var indexed = reader.GetInt64(1);
                if (sequential + indexed > 0 || DateTime.UtcNow > deadline)
                    return (sequential, indexed);
            }

            await Task.Delay(100, Ct);
        }
    }
}

/// <summary>
/// The matching semantics the access path has to preserve. Without these, an index that resolved
/// wildcard boundaries very quickly and wrongly would pass every assertion above.
/// </summary>
public sealed class AppendPrefixBoundarySemanticsTests(PrefixBoundarySemanticsFixture fixture)
    : IClassFixture<PrefixBoundarySemanticsFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEventStoreBackend CreateBackend() => new PostgresEventStoreBackend(fixture.DataSource);

    private static IEventToPersist Event(string eventType, params string[] tags) =>
        new EventToPersist
        {
            EventType = new EventType(eventType),
            EventData = """{"test": true}""",
            Tags = tags.Select(EventTag.Parse).ToArray(),
            Metadata = new Dictionary<string, string>(),
        };

    /// <summary>
    /// A wildcard boundary covers exactly one concept. The concepts here were chosen because
    /// they share a leading substring with it, which is what any prefix- or range-based
    /// matching has to keep apart.
    /// </summary>
    [Theory]
    [InlineData("order-archive")]
    [InlineData("orders")]
    [InlineData("ordering")]
    [InlineData("orde")]
    public async Task WildcardBoundary_IgnoresAConceptThatMerelySharesALeadingSubstring(
        string neighbouringConcept)
    {
        var backend = CreateBackend();
        var run = Guid.NewGuid().ToString("N")[..8];

        var head = (await backend.AppendAsync([Event("seed", $"sem{run}:0")], cancellationToken: Ct))
            .Last().GlobalPosition;

        await backend.AppendAsync(
            [Event("noise", $"{neighbouringConcept}{run}:1")], cancellationToken: Ct);

        var appended = await backend.AppendAsync(
            [Event("order-placed", $"order{run}:1")],
            DcbQuery.Empty.WithTagPrefix($"order{run}"),
            head,
            Ct);

        appended.Should().HaveCount(1,
            because: $"'{neighbouringConcept}{run}:1' is not a tag of concept 'order{run}'");
    }

    /// <summary>
    /// The other direction: a tag that really is of the boundary's concept must still conflict.
    /// Without this, the test above would pass just as well against a boundary that matches
    /// nothing at all.
    /// </summary>
    [Fact]
    public async Task WildcardBoundary_ConflictsOnAnyTagOfItsOwnConcept()
    {
        var backend = CreateBackend();
        var run = Guid.NewGuid().ToString("N")[..8];

        var head = (await backend.AppendAsync([Event("seed", $"sem{run}:0")], cancellationToken: Ct))
            .Last().GlobalPosition;

        await backend.AppendAsync([Event("noise", $"order{run}:some-other-id")], cancellationToken: Ct);

        var append = () => backend.AppendAsync(
            [Event("order-placed", $"order{run}:1")],
            DcbQuery.Empty.WithTagPrefix($"order{run}"),
            head,
            Ct);

        await append.Should().ThrowAsync<DcbConflictException>(
            because: "a wildcard boundary covers every id of its concept, not just the one being written");
    }
}
