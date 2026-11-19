using System.Text.Json.Nodes;
using Alberto.Postgres;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Since migration 011 the backend calls the <c>_lean</c> append functions, which return only
/// each row's position and timestamp, and builds the envelopes from what it sent
/// (codest-be/alberto#269). What an append
/// returns must still equal what a read of the same events returns, field for field and in input
/// order, for every append function shape.
/// </summary>
public abstract class AppendResultSpecification
{
    private const string Type = "ar-match";

    protected abstract Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IReadOnlyList<EventToPersist> events, DcbQuery? query, long? expectedPosition);

    protected abstract Task<IReadOnlyCollection<IEventEnvelope>> Read(DcbQuery query);

    /// <summary>Calls the original, full-row <c>alberto_append_events</c> directly.</summary>
    protected abstract NpgsqlCommand OriginalAppendCommand(string eventsJson);

    private static EventTag Fresh(string key) => new(key, Guid.NewGuid().ToString());

    private static EventToPersist Event(int n, params EventTag[] tags) => new()
    {
        EventType = new EventType(Type),
        Tags = [.. tags, EventTag.ForVersion(2)],
        // Whitespace and key order jsonb does not keep: only JSON equality is promised.
        EventData = $$"""{ "z": {{n}},  "a": { "nested": [1, 2, "three"] }, "s": "éé" }""",
        Metadata = new Dictionary<string, string> { ["correlation"] = $"c-{n}", ["n"] = n.ToString() },
    };

    private async Task AssertAppendMatchesRead(Func<EventTag, EventTag, DcbQuery?> query)
    {
        var order = Fresh("ar-order");
        var region = Fresh("ar-region");
        var sent = Enumerable.Range(0, 5).Select(i => Event(i, order, region)).ToList();

        var appended = (await Append(sent, query(order, region), query(order, region) is null ? null : 0)).ToList();
        var read = (await Read(DcbQuery.ByTags(order))).ToList();

        Assert.Equal(sent.Count, appended.Count);
        Assert.Equal(sent.Select(e => e.Id), appended.Select(e => e.Id));
        Assert.Equal(read.Count, appended.Count);
        for (var i = 0; i < read.Count; i++)
        {
            var a = appended[i];
            var r = read[i];
            Assert.Equal(r.Id, a.Id);
            Assert.Equal(r.TenantId, a.TenantId);
            Assert.Equal(r.GlobalPosition, a.GlobalPosition);
            Assert.Equal(r.EventType.Id, a.EventType.Id);
            Assert.Equal(r.EventType.Version, a.EventType.Version);
            Assert.Equal(2, a.EventType.Version);
            Assert.Equal(r.Tags, a.Tags);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(r.EventData), JsonNode.Parse(a.EventData)), $"{r.EventData} != {a.EventData}");
            Assert.Equal(r.Metadata.OrderBy(kv => kv.Key), a.Metadata.OrderBy(kv => kv.Key));
            Assert.Equal(r.CreatedAt, a.CreatedAt);
        }
        Assert.Equal(appended.Select(e => e.GlobalPosition).Order(), appended.Select(e => e.GlobalPosition));
    }

    [Fact]
    public Task Unchecked_append_returns_what_a_read_returns() =>
        AssertAppendMatchesRead((_, _) => null);

    [Fact]
    public Task Any_tag_append_returns_what_a_read_returns() =>
        AssertAppendMatchesRead((order, region) => DcbQuery.ByTags(order, region));

    [Fact]
    public Task All_tags_append_returns_what_a_read_returns() =>
        AssertAppendMatchesRead((order, region) => DcbQuery.ByAllTags(order, region));

    [Fact]
    public Task Types_and_any_tag_append_returns_what_a_read_returns() =>
        AssertAppendMatchesRead((order, region) => DcbQuery.ByTags(order, region).WithTypes(Type));

    [Fact]
    public Task Types_and_all_tags_append_returns_what_a_read_returns() =>
        AssertAppendMatchesRead((order, region) => DcbQuery.ByAllTags(order, region).WithTypes(Type));

    // The original functions stay for replicas on pre-011 builds during a rolling deploy.
    // Removing or reshaping them would fail those replicas' appends after the write committed.
    [Fact]
    public async Task The_original_full_row_append_function_is_still_there()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        var tag = Fresh("ar-order");
        await using var cmd = OriginalAppendCommand(
            $$$"""[{"event_id":"{{{id}}}","event_type":"{{{Type}}}","event_tags":["{{{tag.Value}}}"],"event_data":{"n":7},"event_metadata":{"k":"v"}}]""");
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(id, reader.GetGuid(reader.GetOrdinal("event_id")));
        Assert.Equal(Type, reader.GetString(reader.GetOrdinal("event_type")));
        Assert.Equal([tag.Value], reader.GetFieldValue<string[]>(reader.GetOrdinal("event_tags")));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"n\":7}"), JsonNode.Parse(reader.GetString(reader.GetOrdinal("event_data")))));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"k\":\"v\"}"), JsonNode.Parse(reader.GetString(reader.GetOrdinal("event_metadata")))));
        Assert.True(reader.GetInt64(reader.GetOrdinal("global_position")) > 0);
        Assert.False(await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task A_conflict_still_reports_its_position()
    {
        var order = Fresh("ar-order");
        var query = DcbQuery.ByTags(order).WithTypes(Type);
        var expected = (await Append([Event(0, order)], null, null)).Single().GlobalPosition;
        var conflicting = (await Append([Event(1, order)], null, null)).Single().GlobalPosition;

        var ex = await Assert.ThrowsAsync<DcbConflictException>(() => Append([Event(2, order)], query, expected));
        Assert.Equal(conflicting.ToString(), Assert.IsType<PostgresException>(ex.InnerException).Hint);
        Assert.Equal(conflicting, ex.ConflictingPosition);
    }
}

[Trait("Category", "Integration")]
public sealed class SingleTenantAppendResultTests(SingleTenantPostgresFixture fixture)
    : AppendResultSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    private readonly PostgresEventStoreBackend _backend = new(fixture.DataSource);

    protected override Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IReadOnlyList<EventToPersist> events, DcbQuery? query, long? expectedPosition) =>
        _backend.AppendAsync(events, query, expectedPosition, TestContext.Current.CancellationToken);

    protected override Task<IReadOnlyCollection<IEventEnvelope>> Read(DcbQuery query) =>
        _backend.ReadAsync(query, 0, 100, TestContext.Current.CancellationToken);

    protected override NpgsqlCommand OriginalAppendCommand(string eventsJson)
    {
        var cmd = fixture.DataSource.CreateCommand("SELECT * FROM alberto_append_events($1::jsonb)");
        cmd.Parameters.AddWithValue(eventsJson);
        return cmd;
    }
}

[Trait("Category", "Integration")]
public sealed class MultiTenantAppendResultTests(MultiTenantDbFixture fixture)
    : AppendResultSpecification, IClassFixture<MultiTenantDbFixture>
{
    private const string Tenant = "ar-tenant";
    private readonly PostgresTenantEventStoreBackend _backend = new(fixture.DataSource);

    protected override Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IReadOnlyList<EventToPersist> events, DcbQuery? query, long? expectedPosition) =>
        _backend.AppendForTenant(Tenant, events, query, expectedPosition, TestContext.Current.CancellationToken);

    protected override Task<IReadOnlyCollection<IEventEnvelope>> Read(DcbQuery query) =>
        _backend.ReadForTenant(Tenant, query, 0, 100, TestContext.Current.CancellationToken);

    protected override NpgsqlCommand OriginalAppendCommand(string eventsJson)
    {
        var cmd = fixture.DataSource.CreateCommand("SELECT * FROM alberto_append_events($1, $2::jsonb)");
        cmd.Parameters.AddWithValue(Tenant);
        cmd.Parameters.AddWithValue(eventsJson);
        return cmd;
    }

    private async Task<(uint Xmin, uint Xmax, DateTimeOffset LastSeen)?> TenantRow(string tenant)
    {
        await using var cmd = fixture.DataSource.CreateCommand(
            "SELECT xmin::text::bigint, xmax::text::bigint, last_seen_at FROM alberto_tenants WHERE tenant_id = $1");
        cmd.Parameters.AddWithValue(tenant);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken)) return null;
        return ((uint)reader.GetInt64(0), (uint)reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    [Fact]
    public async Task A_new_tenants_first_append_creates_its_row_and_a_second_append_leaves_it_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = $"ar-{Guid.NewGuid():N}";
        var tag = new EventTag("ar-order", Guid.NewGuid().ToString());
        IEventToPersist Probe() => new EventToPersist { EventType = new EventType("ar-match"), Tags = [tag], EventData = "{}" };

        Assert.Null(await TenantRow(tenant));
        await _backend.AppendForTenant(tenant, [Probe()], cancellationToken: ct);
        var created = await TenantRow(tenant);
        Assert.NotNull(created);

        await _backend.AppendForTenant(tenant, [Probe(), Probe()], cancellationToken: ct);
        Assert.Equal(created, await TenantRow(tenant));
    }

    [Fact]
    public async Task A_stale_tenant_row_is_refreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = $"ar-{Guid.NewGuid():N}";
        IEventToPersist Probe() => new EventToPersist { EventType = new EventType("ar-match"), Tags = [], EventData = "{}" };

        await _backend.AppendForTenant(tenant, [Probe()], cancellationToken: ct);
        await using (var age = fixture.DataSource.CreateCommand(
            "UPDATE alberto_tenants SET last_seen_at = now() - INTERVAL '1 hour' WHERE tenant_id = $1"))
        {
            age.Parameters.AddWithValue(tenant);
            await age.ExecuteNonQueryAsync(ct);
        }
        var aged = await TenantRow(tenant);

        await _backend.AppendForTenant(tenant, [Probe()], cancellationToken: ct);
        var refreshed = await TenantRow(tenant);
        Assert.True(refreshed!.Value.LastSeen > aged!.Value.LastSeen.AddMinutes(30));
    }
}
