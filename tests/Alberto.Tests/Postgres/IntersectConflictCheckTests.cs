using Alberto.Postgres;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// The types-AND-tags conflict checks of <c>alberto_append_events_v4</c> (any tag) and
/// <c>_v6</c> (all tags), as rewritten by migration 009 to drive from the tag index bounded by
/// the expected position (codest-be/alberto#266). Each test uses fresh tags, so the shared
/// fixture database cannot leak into the result.
/// </summary>
public abstract class IntersectConflictCheckSpecification
{
    private const string Match = "icc-match";
    private const string Other = "icc-other";

    protected abstract Task<long> Append(string type, params EventTag[] tags);

    protected abstract Task<IReadOnlyCollection<IEventEnvelope>> AppendChecked(DcbQuery query, long expectedPosition);

    protected static EventToPersist Event(string type, params EventTag[] tags) => new()
    {
        EventType = new EventType(type),
        Tags = tags,
        EventData = "{}",
    };

    private static EventTag Fresh(string key) => new(key, Guid.NewGuid().ToString());

    private async Task AssertConflictAt(DcbQuery query, long expectedPosition, long conflictingPosition)
    {
        var ex = await Assert.ThrowsAsync<DcbConflictException>(() => AppendChecked(query, expectedPosition));
        var pg = Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
        Assert.Equal(conflictingPosition.ToString(), pg.Hint);
        Assert.Equal(conflictingPosition, ex.ConflictingPosition);
    }

    // ── _v4: types AND any tag ──────────────────────────────────────────────

    [Fact]
    public async Task AnyTag_reports_the_lowest_conflict_after_the_expected_position()
    {
        var order = Fresh("icc-order");
        var second = Fresh("icc-second");

        var before = await Append(Match, order);
        await Append(Other, order);
        var lowest = await Append(Match, second); // carries only the second listed tag
        await Append(Match, order);

        await AssertConflictAt(DcbQuery.ByTags(order, second).WithTypes(Match), before, lowest);
    }

    [Fact]
    public async Task AnyTag_does_not_conflict_on_a_type_only_or_tag_only_match()
    {
        var order = Fresh("icc-order");

        var expected = await Append(Match, order);
        await Append(Match, Fresh("icc-unrelated"));
        await Append(Other, order);

        var appended = await AppendChecked(DcbQuery.ByTags(order).WithTypes(Match), expected);
        Assert.Single(appended);
    }

    [Fact]
    public async Task AnyTag_at_position_zero_appends_on_a_tag_busy_with_other_types()
    {
        var order = Fresh("icc-order");
        for (var i = 0; i < 10; i++)
            await Append(Other, order);

        var appended = await AppendChecked(DcbQuery.ByTags(order).WithTypes(Match), 0);
        Assert.Single(appended);

        // The event just written now trips the same check from position zero.
        await AssertConflictAt(DcbQuery.ByTags(order).WithTypes(Match), 0, appended.Single().GlobalPosition);
    }

    // ── _v6: types AND all tags ─────────────────────────────────────────────

    [Fact]
    public async Task AllTags_reports_the_lowest_conflict_after_the_expected_position()
    {
        var order = Fresh("icc-order");
        var region = Fresh("icc-region");

        var before = await Append(Match, order, region);
        await Append(Other, order, region);
        await Append(Match, order); // right type, only one of the tags
        var lowest = await Append(Match, order, region);
        await Append(Match, order, region);

        await AssertConflictAt(DcbQuery.ByAllTags(order, region).WithTypes(Match), before, lowest);
    }

    [Fact]
    public async Task AllTags_does_not_conflict_on_a_type_only_or_tag_only_match()
    {
        var order = Fresh("icc-order");
        var region = Fresh("icc-region");

        var expected = await Append(Match, order, region);
        await Append(Match, order);
        await Append(Match, Fresh("icc-unrelated"));
        await Append(Other, order, region);

        var appended = await AppendChecked(DcbQuery.ByAllTags(order, region).WithTypes(Match), expected);
        Assert.Single(appended);
    }

    [Fact]
    public async Task AllTags_at_position_zero_appends_on_tags_busy_with_other_types()
    {
        var order = Fresh("icc-order");
        var region = Fresh("icc-region");
        for (var i = 0; i < 10; i++)
            await Append(Other, order, region);

        var appended = await AppendChecked(DcbQuery.ByAllTags(order, region).WithTypes(Match), 0);
        Assert.Single(appended);

        await AssertConflictAt(DcbQuery.ByAllTags(order, region).WithTypes(Match), 0, appended.Single().GlobalPosition);
    }
}

[Trait("Category", "Integration")]
public sealed class SingleTenantIntersectConflictCheckTests(SingleTenantPostgresFixture fixture)
    : IntersectConflictCheckSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    private readonly PostgresEventStoreBackend _backend = new(fixture.DataSource);

    protected override async Task<long> Append(string type, params EventTag[] tags) =>
        (await _backend.AppendAsync([Event(type, tags)], cancellationToken: TestContext.Current.CancellationToken))
        .Single().GlobalPosition;

    protected override Task<IReadOnlyCollection<IEventEnvelope>> AppendChecked(DcbQuery query, long expectedPosition) =>
        _backend.AppendAsync([Event("icc-match", query.Tags.ToArray())], query, expectedPosition,
            TestContext.Current.CancellationToken);
}

[Trait("Category", "Integration")]
public sealed class MultiTenantIntersectConflictCheckTests(MultiTenantDbFixture fixture)
    : IntersectConflictCheckSpecification, IClassFixture<MultiTenantDbFixture>
{
    private const string Tenant = "icc-tenant";
    private readonly PostgresTenantEventStoreBackend _backend = new(fixture.DataSource);

    protected override async Task<long> Append(string type, params EventTag[] tags) =>
        (await _backend.AppendForTenant(Tenant, [Event(type, tags)], cancellationToken: TestContext.Current.CancellationToken))
        .Single().GlobalPosition;

    protected override Task<IReadOnlyCollection<IEventEnvelope>> AppendChecked(DcbQuery query, long expectedPosition) =>
        _backend.AppendForTenant(Tenant, [Event("icc-match", query.Tags.ToArray())], query, expectedPosition,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_matching_event_in_another_tenant_does_not_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var order = new EventTag("icc-order", Guid.NewGuid().ToString());
        var region = new EventTag("icc-region", Guid.NewGuid().ToString());

        await _backend.AppendForTenant("icc-other-tenant", [Event("icc-match", order, region)],
            cancellationToken: cancellationToken);

        foreach (var query in new[] { DcbQuery.ByTags(order).WithTypes("icc-match"), DcbQuery.ByAllTags(order, region).WithTypes("icc-match") })
        {
            var appended = await _backend.AppendForTenant(Tenant, [Event("icc-other", order, region)], query, 0, cancellationToken);
            Assert.Single(appended);
        }
    }
}
