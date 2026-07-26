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
public sealed class ReadPrefixBoundarySemanticsFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// Pins how a wildcard tag boundary (<c>order:*</c>) reaches the tag table when reading.
/// </summary>
/// <remarks>
/// Migration 023 stopped the append path matching wildcard boundaries with
/// <c>tag LIKE prefix || '%'</c>; the read functions kept it. The reason the LIKE cannot use an
/// index is the same on both sides — the pattern is a value from the unnested prefix array rather
/// than a constant, so PostgreSQL has no range to derive and scans the whole tag table. The cost
/// lands differently, though: a read is not inside the append transaction, so it adds no latency
/// to writers. What it does is make a query's cost a function of total history rather than of how
/// much it matches — the shape that looks fine in development and degrades continuously.
/// <para>
/// Only two of the three wildcard read functions are asserted on here.
/// <c>alberto_read_by_types_or_tag_patterns</c> composes its axes with OR across two LEFT JOINs,
/// so it has to visit every event in the position range whatever the tag predicate says; there is
/// no access path for the concept index to win. Measured before and after the rewrite it is
/// unchanged — one sequential scan of the tag table either way. Making that shape indexable means
/// restructuring it as a union of per-axis lookups, which is a different change; the rewrite still
/// applies to it for consistency, and its matching is covered by the semantic tests below.
/// </para>
/// <para>
/// Every test here takes a database of its own rather than sharing a class fixture. The evidence
/// is <c>pg_stat_user_tables</c> and <c>pg_stat_user_indexes</c>, which are per database and not
/// per session: two tests measuring at once would reset each other's counters and read each
/// other's scans.
/// </para>
/// </remarks>
public sealed class ReadPrefixBoundaryAccessPathTests(PostgresCluster cluster)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Tenant = "tenant-a";
    private const string ConceptIndex = "ix_alberto_event_tag_positions_concept";

    /// <summary>
    /// A wildcard read must resolve its boundary through the concept index. Reaching the tag table
    /// some other way is not enough to assert on: both shapes below join on
    /// <c>global_position</c> as well, so the table is touched through an index either way — what
    /// distinguishes a lookup from a scan of all history is which index answered the boundary.
    /// </summary>
    [Theory]
    [InlineData("tags-only")]
    [InlineData("types-and-tags")]
    public async Task WildcardBoundaryRead_ResolvesTheBoundaryThroughTheConceptIndex(string shape)
    {
        await using var dataSource = await PrivateDatabaseAsync(PostgresTemplates.SingleTenant);
        await SeedAsync(dataSource, tenant: null);

        var backend = new PostgresEventStoreBackend(dataSource);

        await ResetStatisticsAsync(dataSource);
        var read = await backend.StreamAsync(QueryFor(shape), cancellationToken: Ct);

        read.Should().NotBeEmpty(because: "an access path is only evidence if the query matched");

        var (sequential, _) = await TagTableScansAsync(dataSource);
        var conceptLookups = await ConceptIndexScansAsync(dataSource);

        conceptLookups.Should().BeGreaterThan(0,
            because: "the wildcard boundary should be a lookup of one concept");
        sequential.Should().Be(0,
            because: "a read's cost should follow what it matches, not the whole tag history");
    }

    /// <summary>
    /// The same guarantee for the tenant-scoped read functions. The multi-tenant migration set
    /// carries its own definitions of them, so fixing one tree says nothing about the other.
    /// These call the functions directly rather than through a backend: the tenant-scoped backend
    /// is internal and needs the tenancy decorator chain around it, and what is under test is
    /// the SQL.
    /// </summary>
    [Theory]
    [InlineData("alberto_read_by_tag_patterns(@tenant, NULL, @prefixes, 0, NULL)")]
    [InlineData("alberto_read_by_types_and_tag_patterns(@tenant, @types, NULL, @prefixes, 0, NULL)")]
    public async Task TenantScopedWildcardBoundaryRead_ResolvesTheBoundaryThroughTheConceptIndex(
        string call)
    {
        await using var dataSource = await PrivateDatabaseAsync(PostgresTemplates.MultiTenant);
        await SeedAsync(dataSource, Tenant);

        await using var conn = await dataSource.OpenConnectionAsync(Ct);
        await new NpgsqlCommand("SELECT pg_stat_reset()", conn).ExecuteNonQueryAsync(Ct);

        await using var read = new NpgsqlCommand($"SELECT count(*) FROM {call}", conn);
        read.Parameters.AddWithValue("tenant", Tenant);
        read.Parameters.AddWithValue("types", new[] { WantedType });
        read.Parameters.AddWithValue("prefixes", new[] { $"{WantedConcept}:" });
        var matched = (long)(await read.ExecuteScalarAsync(Ct))!;

        matched.Should().BeGreaterThan(0,
            because: "an access path is only evidence if the query matched");

        var (sequential, _) = await TagTableScansAsync(dataSource);
        var conceptLookups = await ConceptIndexScansAsync(dataSource);

        conceptLookups.Should().BeGreaterThan(0,
            because: "the wildcard boundary should be a lookup of one concept");
        sequential.Should().Be(0,
            because: "the tenant-scoped read functions have the same unbounded-scan problem");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private const string WantedType = "path-wanted";
    private const string WantedConcept = "pathread";

    /// <summary>
    /// Concepts the seeded tags are spread over. The first three share a leading substring with
    /// the one being read, so an access path that confuses them shows up here rather than only in
    /// the semantic tests; the rest are filler, there to make the read selective enough that a
    /// planner has a reason to prefer a lookup over a scan.
    /// </summary>
    private static readonly string[] Concepts =
    [
        WantedConcept, "pathread-archive", "pathreads", "pathcustomer",
        .. Enumerable.Range(0, 16).Select(i => $"pathfiller{i}"),
    ];

    /// <summary>
    /// Events to seed. Enough that a sequential scan is the more expensive plan, and no more —
    /// four of these tests run against four cloned databases alongside the rest of the suite.
    /// </summary>
    private const int SeededEvents = 5000;

    private static DcbQuery QueryFor(string shape) => shape switch
    {
        "tags-only" => DcbQuery.Empty.WithTagPrefix(WantedConcept),
        "types-and-tags" => DcbQuery.Empty.WithTypes(WantedType).WithTagPrefix(WantedConcept),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown read shape"),
    };

    private async Task<NpgsqlDataSource> PrivateDatabaseAsync(PostgresTemplate template) =>
        NpgsqlDataSource.Create(await cluster.CloneAsync(template, "readprefixboundary", Ct));

    /// <summary>
    /// Seeds tag rows across concepts that share a leading substring, straight through SQL — the
    /// access path under test does not depend on how the rows got there, and a planner only
    /// prefers a lookup over a scan once there is enough history for the difference to matter.
    /// </summary>
    /// <remarks>
    /// <paramref name="tenant"/> is null for the single-tenant schema, which has no tenant column.
    /// Every event carries the type the read filters on, so the type axis cannot be what narrows
    /// the result — the boundary has to.
    /// </remarks>
    private static async Task SeedAsync(NpgsqlDataSource dataSource, string? tenant)
    {
        var tenantColumn = tenant is null ? "" : "tenant_id, ";
        var tenantValue = tenant is null ? "" : "@tenant, ";
        var tenantFilter = tenant is null ? "" : "AND e.tenant_id = @tenant";

        await using var conn = await dataSource.OpenConnectionAsync(Ct);
        await using var seed = new NpgsqlCommand(
            $"""
            INSERT INTO alberto_events ({tenantColumn}event_type)
            SELECT {tenantValue}'{WantedType}'
            FROM generate_series(1, {SeededEvents});

            INSERT INTO alberto_event_type_positions ({tenantColumn}event_type, global_position)
            SELECT {tenantValue}'{WantedType}', e.global_position
            FROM alberto_events e
            WHERE TRUE {tenantFilter};

            INSERT INTO alberto_event_tag_positions ({tenantColumn}tag, global_position)
            SELECT {tenantValue}
                   (ARRAY['{string.Join("', '", Concepts)}'])
                       [1 + (e.global_position % {Concepts.Length})]
                       || ':' || e.global_position::text,
                   e.global_position
            FROM alberto_events e
            WHERE TRUE {tenantFilter};

            ANALYZE alberto_events;
            ANALYZE alberto_event_type_positions;
            ANALYZE alberto_event_tag_positions;
            """,
            conn);

        if (tenant is not null)
            seed.Parameters.AddWithValue("tenant", tenant);

        await seed.ExecuteNonQueryAsync(Ct);
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
    /// A backend flushes its statistics when it goes idle, which is not synchronous with the read
    /// returning, so this waits for the read to be accounted for at all before reading the split.
    /// Waiting on the total rather than on either counter keeps the wait independent of which
    /// access path was taken — otherwise "no sequential scan" would be indistinguishable from
    /// "not flushed yet".
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

    /// <summary>
    /// How many times the concept index answered a query. Called only after
    /// <see cref="TagTableScansAsync"/> has established that the read is accounted for, so this
    /// needs no wait of its own.
    /// </summary>
    private static async Task<long> ConceptIndexScansAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT coalesce(idx_scan, 0)
            FROM pg_stat_user_indexes
            WHERE indexrelname = @index
            """,
            conn);
        cmd.Parameters.AddWithValue("index", ConceptIndex);

        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

/// <summary>
/// The matching semantics the access path has to preserve. Without these, an index that resolved
/// wildcard boundaries very quickly and wrongly would pass every assertion above. All three
/// wildcard read functions are covered, including the union shape the access-path tests leave
/// alone — the rewrite reaches its predicate too, so its matching has to be pinned.
/// </summary>
public sealed class ReadPrefixBoundarySemanticsTests(ReadPrefixBoundarySemanticsFixture fixture)
    : IClassFixture<ReadPrefixBoundarySemanticsFixture>
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
    /// A wildcard boundary covers exactly one concept. The concepts here were chosen because they
    /// share a leading substring with it, which is what any prefix- or range-based matching has to
    /// keep apart. Asserted for all three wildcard read shapes, because each carries its own copy
    /// of the predicate.
    /// </summary>
    [Theory]
    [InlineData("tags-only", "order-archive")]
    [InlineData("tags-only", "orders")]
    [InlineData("tags-only", "ordering")]
    [InlineData("tags-only", "orde")]
    [InlineData("types-and-tags", "order-archive")]
    [InlineData("types-and-tags", "orders")]
    [InlineData("types-and-tags", "ordering")]
    [InlineData("types-and-tags", "orde")]
    [InlineData("types-or-tags", "order-archive")]
    [InlineData("types-or-tags", "orders")]
    [InlineData("types-or-tags", "ordering")]
    [InlineData("types-or-tags", "orde")]
    public async Task WildcardBoundaryRead_IgnoresAConceptThatMerelySharesALeadingSubstring(
        string shape, string neighbouringConcept)
    {
        var backend = CreateBackend();
        var run = Guid.NewGuid().ToString("N")[..8];

        await backend.AppendAsync([Event($"wanted{run}", $"order{run}:1")], cancellationToken: Ct);

        // A type the query does not ask for either, so the union shape has no other way to let
        // this event in.
        await backend.AppendAsync(
            [Event($"noise{run}", $"{neighbouringConcept}{run}:1")], cancellationToken: Ct);

        var read = await backend.StreamAsync(QueryFor(shape, run), cancellationToken: Ct);

        read.SelectMany(e => e.Tags).Select(t => t.ToString())
            .Should().NotContain($"{neighbouringConcept}{run}:1",
                because: $"'{neighbouringConcept}{run}:1' is not a tag of concept 'order{run}'");
    }

    /// <summary>
    /// The other direction: every tag that really is of the boundary's concept must come back.
    /// Without this, the test above would pass just as well against a boundary that matches
    /// nothing at all.
    /// </summary>
    [Theory]
    [InlineData("tags-only")]
    [InlineData("types-and-tags")]
    [InlineData("types-or-tags")]
    public async Task WildcardBoundaryRead_ReturnsEveryTagOfItsOwnConcept(string shape)
    {
        var backend = CreateBackend();
        var run = Guid.NewGuid().ToString("N")[..8];

        await backend.AppendAsync([Event($"wanted{run}", $"order{run}:1")], cancellationToken: Ct);
        await backend.AppendAsync(
            [Event($"wanted{run}", $"order{run}:some-other-id")], cancellationToken: Ct);

        var read = await backend.StreamAsync(QueryFor(shape, run), cancellationToken: Ct);

        read.SelectMany(e => e.Tags).Select(t => t.ToString())
            .Should().Contain([$"order{run}:1", $"order{run}:some-other-id"],
                because: "a wildcard boundary covers every id of its concept, not just one");
    }

    private static DcbQuery QueryFor(string shape, string run) => shape switch
    {
        "tags-only" => DcbQuery.Empty.WithTagPrefix($"order{run}"),
        "types-and-tags" => DcbQuery.Empty.WithTypes($"wanted{run}").WithTagPrefix($"order{run}"),
        "types-or-tags" =>
            DcbQuery.Empty.WithTypes($"wanted{run}").WithTagPrefix($"order{run}").AsUnion(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown read shape"),
    };
}
