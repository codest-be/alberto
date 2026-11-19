using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// The typed read a processor polls with (<c>ReadAllOfTypesAsync</c>), driven from
/// <c>alberto_event_type_positions</c> (codest-be/alberto#268). Each test uses fresh types,
/// so the shared fixture database cannot leak into the result.
/// </summary>
public abstract class TypedReadSpecification
{
    protected abstract NpgsqlDataSource DataSource { get; }

    protected abstract bool Tenanted { get; }

    /// <summary>Appends one event per type, in order, spread over tenants where the store has them.</summary>
    protected abstract Task<IReadOnlyList<long>> Append(params string[] types);

    protected abstract Task<IReadOnlyCollection<IEventEnvelope>> Read(IReadOnlySet<string> types, long after, int limit);

    private static string Fresh(string name) => $"tr-{name}-{Guid.NewGuid():N}";

    [Fact]
    public async Task Several_types_come_back_interleaved_in_position_order()
    {
        var (a, b, other) = (Fresh("a"), Fresh("b"), Fresh("other"));
        var positions = await Append(a, other, b, a, other, b, b, a);

        var result = await Read(new HashSet<string> { a, b }, 0, 100);

        Assert.Equal(
            new[] { positions[0], positions[2], positions[3], positions[5], positions[6], positions[7] },
            result.Select(e => e.GlobalPosition));
        Assert.Equal(new[] { a, b, a, b, b, a }, result.Select(e => e.EventType.Id));
    }

    [Fact]
    public async Task The_limit_takes_the_lowest_positions_over_all_types()
    {
        var (a, b) = (Fresh("a"), Fresh("b"));
        var positions = await Append(a, a, a, b, a, b);

        var result = await Read(new HashSet<string> { a, b }, 0, 4);

        Assert.Equal(positions.Take(4), result.Select(e => e.GlobalPosition));
    }

    [Fact]
    public async Task Only_events_after_the_position_are_read()
    {
        var a = Fresh("a");
        var positions = await Append(a, a, a);

        var result = await Read(new HashSet<string> { a }, positions[0], 100);

        Assert.Equal(positions.Skip(1), result.Select(e => e.GlobalPosition));
    }

    [Fact]
    public async Task An_absent_type_reads_nothing_and_does_not_disturb_a_present_one()
    {
        var (present, absent) = (Fresh("present"), Fresh("absent"));
        var positions = await Append(present, Fresh("other"));

        Assert.Empty(await Read(new HashSet<string> { absent }, 0, 100));
        Assert.Equal(
            new[] { positions[0] },
            (await Read(new HashSet<string> { present, absent }, 0, 100)).Select(e => e.GlobalPosition));
    }

    [Fact]
    public async Task The_envelope_carries_the_event_as_appended()
    {
        var a = Fresh("a");
        var positions = await Append(a);

        var envelope = Assert.Single(await Read(new HashSet<string> { a }, 0, 100));

        Assert.Equal(positions[0], envelope.GlobalPosition);
        Assert.Equal(a, envelope.EventType.Id);
        Assert.Contains(new EventTag("tr", a), envelope.Tags);
    }

    /// <summary>
    /// The plan probes the type index and joins <c>alberto_events</c> by primary key; it never
    /// scans the event log. Seeded with enough noise that the planner prefers indexes to scans.
    /// </summary>
    [Fact]
    public async Task The_read_never_scans_the_event_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var noise = Fresh("noise");
        for (var i = 0; i < 10; i++)
            await Append(Enumerable.Repeat(noise, 500).ToArray());
        var wanted = Fresh("wanted");
        await Append(wanted, wanted);

        await using var connection = await DataSource.OpenConnectionAsync(ct);
        await using (var analyze = new NpgsqlCommand("ANALYZE alberto_events; ANALYZE alberto_event_type_positions;", connection))
            await analyze.ExecuteNonQueryAsync(ct);

        await using var explain = new NpgsqlCommand(
            "EXPLAIN " + PostgresBackendHelpers.ReadAllOfTypesSql(new SchemaQualifier(null), Tenanted), connection);
        explain.Parameters.AddWithValue("after", 0L);
        explain.Parameters.AddWithValue("types", new[] { wanted, Fresh("absent") });
        explain.Parameters.Add(new NpgsqlParameter("limit", NpgsqlTypes.NpgsqlDbType.Integer) { Value = 100 });

        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                plan.Add(reader.GetString(0));
        var text = string.Join('\n', plan);

        Assert.DoesNotContain("Seq Scan", text);
        Assert.Contains("on alberto_event_type_positions", text);
    }
}

[Trait("Category", "Integration")]
public sealed class SingleTenantTypedReadTests(SingleTenantPostgresFixture fixture)
    : TypedReadSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    private readonly PostgresEventStoreBackend _backend = new(fixture.DataSource);

    protected override NpgsqlDataSource DataSource => fixture.DataSource;

    protected override bool Tenanted => false;

    protected override async Task<IReadOnlyList<long>> Append(params string[] types) =>
        (await _backend.AppendAsync(
            types.Select(t => new EventToPersist { EventType = new EventType(t), Tags = [new EventTag("tr", t)], EventData = "{}" }),
            cancellationToken: TestContext.Current.CancellationToken))
        .Select(e => e.GlobalPosition).ToList();

    protected override Task<IReadOnlyCollection<IEventEnvelope>> Read(IReadOnlySet<string> types, long after, int limit) =>
        _backend.ReadAllOfTypesAsync(types, after, limit, TestContext.Current.CancellationToken);
}

[Trait("Category", "Integration")]
public sealed class MultiTenantTypedReadTests(MultiTenantDbFixture fixture)
    : TypedReadSpecification, IClassFixture<MultiTenantDbFixture>
{
    private readonly PostgresTenantEventStoreBackend _backend = new(fixture.DataSource);

    protected override NpgsqlDataSource DataSource => fixture.DataSource;

    protected override bool Tenanted => true;

    // Each run of one type goes to the other tenant than the run before, in one append, so every
    // multi-type test reads across tenants while the EXPLAIN seed stays a handful of round trips.
    protected override async Task<IReadOnlyList<long>> Append(params string[] types)
    {
        var positions = new List<long>();
        var tenant = 0;
        for (var start = 0; start < types.Length; tenant ^= 1)
        {
            var end = start;
            while (end < types.Length && types[end] == types[start]) end++;
            var appended = await _backend.AppendForTenant(
                tenant == 0 ? "tr-tenant-a" : "tr-tenant-b",
                types[start..end].Select(t => new EventToPersist { EventType = new EventType(t), Tags = [new EventTag("tr", t)], EventData = "{}" }),
                cancellationToken: TestContext.Current.CancellationToken);
            positions.AddRange(appended.Select(e => e.GlobalPosition));
            start = end;
        }
        return positions;
    }

    protected override Task<IReadOnlyCollection<IEventEnvelope>> Read(IReadOnlySet<string> types, long after, int limit) =>
        _backend.ReadAllTenantsOfTypes(types, after, limit, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_tenant_of_each_event_comes_back()
    {
        var (a, b) = (Fresh2(), Fresh2());
        await Append(a, b);

        var result = await Read(new HashSet<string> { a, b }, 0, 100);

        Assert.Equal(new[] { "tr-tenant-a", "tr-tenant-b" }, result.Select(e => e.TenantId));
    }

    private static string Fresh2() => $"tr-t-{Guid.NewGuid():N}";
}
