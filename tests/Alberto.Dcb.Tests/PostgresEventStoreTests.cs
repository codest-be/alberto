using System.Text.Json;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Integration tests for <see cref="EventStore"/> over the Postgres backend.
/// Verifies that events are persisted and projections run immediately after.
/// </summary>
public sealed class PostgresEventStoreTests(SingleTenantPostgresFixture fixture) : IClassFixture<SingleTenantPostgresFixture>
{
    #region Test Events

    [EventType("order-created")]
    public record OrderCreated(Guid OrderId, decimal Amount) : IEvent;

    [EventType("order-confirmed")]
    public record OrderConfirmed(Guid OrderId) : IEvent;

    [EventType("order-cancelled")]
    public record OrderCancelled(Guid OrderId) : IEvent;

    // Types-only reads cannot be narrowed by a tag, so they see every event of a named type in
    // the fixture database. These three names are used by the alberto_read_by_types regression
    // tests and by nothing else, which is what keeps those assertions exact.
    [EventType("probe-alpha")]
    public record ProbeAlpha(Guid OrderId) : IEvent;

    [EventType("probe-beta")]
    public record ProbeBeta(Guid OrderId) : IEvent;

    [EventType("probe-gamma")]
    public record ProbeGamma(Guid OrderId) : IEvent;

    // The union shapes match on the type axis without a tag to narrow it, so they have the same
    // exactness problem as the types-only reads and the same answer: three more names carried by
    // no other test. The tag-axis tests need no reserved names — they tag with a fresh Guid.
    [EventType("union-alpha")]
    public record UnionAlpha(Guid OrderId) : IEvent;

    [EventType("union-beta")]
    public record UnionBeta(Guid OrderId) : IEvent;

    [EventType("union-gamma")]
    public record UnionGamma(Guid OrderId) : IEvent;

    #endregion

    #region Test State and Projection

    public record OrderSummary
    {
        public Guid OrderId { get; init; }
        public decimal Amount { get; init; }
        public string Status { get; init; } = "";
    }

    private static ProjectionDeclaration<OrderSummary> OrderSummaryDeclaration() =>
        DeclareProjection.For<OrderSummary>("order-summary")
            .On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => new OrderSummary
                {
                    OrderId = e.OrderId,
                    Amount = e.Amount,
                    Status = "Created"
                })
            .On<OrderConfirmed>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => state with { Status = "Confirmed" })
            .Build();

    /// <summary>
    /// Runs a projection declaration inline against an <see cref="IStateStore{TState}"/>.
    /// The non-EF inline path is not wired by the module builder, so the test supplies its own
    /// <see cref="IInlineProjection"/> — folding is delegated to the real batch processor.
    /// </summary>
    private sealed class InlineStateStoreProjection<TState> : IInlineProjection
        where TState : new()
    {
        private readonly DeclaredAsyncProjection<TState> _inner;

        public InlineStateStoreProjection(
            ProjectionDeclaration<TState> declaration,
            IStateStore<TState> stateStore)
        {
            HandledEventTypes = declaration.HandledEventTypes;
            _inner = new DeclaredAsyncProjection<TState>(declaration, _ => stateStore);
        }

        public IReadOnlySet<string> HandledEventTypes { get; }

        public Task ProcessAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
            => _inner.ProcessBatchAsync(events, ct);
    }

    #endregion

    #region Basic Append Tests

    [Fact]
    public async Task AppendAsync_ShouldPersistEvents()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var orderId = Guid.NewGuid();
        var result = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId, 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(orderId, JsonSerializer.Deserialize<OrderCreated>(result.First().EventData)!.OrderId);
    }

    [Fact]
    public async Task AppendAsync_ShouldReturnGlobalPosition()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var result1 = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var result2 = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 200m))],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result2.First().GlobalPosition > result1.First().GlobalPosition);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnAppendedEvents()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());
        await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId, 100m), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var events = await eventStore.StreamAsync(
            DcbQuery.ByTags(tag.Value),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(events);
        Assert.Equal("order-created", events.First().EventType.Id);
    }

    #endregion

    #region Inline Projection Tests

    [Fact]
    public async Task AppendAsync_ShouldRunInlineProjection()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

        var orderId = Guid.NewGuid();
        await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId, 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await stateStore.LoadManyAsync(
            [orderId.ToString()],
            ct: TestContext.Current.CancellationToken);

        Assert.Single(loaded);
        Assert.Equal(orderId, loaded[orderId.ToString()].OrderId);
        Assert.Equal(100m, loaded[orderId.ToString()].Amount);
        Assert.Equal("Created", loaded[orderId.ToString()].Status);
    }

    [Fact]
    public async Task AppendAsync_ShouldUpdateProjectionWithSubsequentEvents()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId, 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        await eventStore.AppendAsync(
            [CreateEvent(new OrderConfirmed(orderId))],
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await stateStore.LoadManyAsync(
            [orderId.ToString()],
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Confirmed", loaded[orderId.ToString()].Status);
        Assert.Equal(100m, loaded[orderId.ToString()].Amount);
    }

    [Fact]
    public async Task AppendAsync_ShouldHandleMultipleEventsInSingleAppend()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync(
            [
                CreateEvent(new OrderCreated(orderId, 100m)),
                CreateEvent(new OrderConfirmed(orderId))
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await stateStore.LoadManyAsync(
            [orderId.ToString()],
            ct: TestContext.Current.CancellationToken);

        Assert.Single(loaded);
        Assert.Equal("Confirmed", loaded[orderId.ToString()].Status);
    }

    [Fact]
    public async Task AppendAsync_ShouldNotRunProjectionsWhenNoRelevantEvents()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

        // OrderCancelled is not handled by OrderSummaryProjection
        await eventStore.AppendAsync(
            [CreateEvent(new OrderCancelled(Guid.NewGuid()))],
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await stateStore.LoadManyAsync(
            ["any-id"],
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(loaded);
    }

    #endregion

    #region DCB Conflict Tests

    /// <summary>
    /// A conflict must arrive with the three facts a caller acts on, not just the type.
    /// </summary>
    /// <remarks>
    /// The position, the expected position and the query are what a retry loop reads to decide
    /// whether to re-derive its decision or give up. The Postgres backend used to report
    /// <c>-1</c>/<c>-1</c>/<c>*</c> for all three because it built the exception from the
    /// message-and-inner constructor, so a retry loop that looked at them was reading
    /// placeholders — and could not tell them apart from a real conflict at position -1.
    /// </remarks>
    [Fact]
    public async Task AppendAsync_WithDcbConflict_ShouldThrowDcbConflictException()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());

        var appended = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId, 100m), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var conflictingPosition = appended.Single().GlobalPosition;

        var dcbQuery = DcbQuery.Empty
            .WithTypes("order-created")
            .WithTags(tag);

        var ex = await Assert.ThrowsAsync<DcbConflictException>(() =>
            eventStore.AppendAsync(
                [CreateEvent(new OrderConfirmed(orderId), tag)],
                dcbQuery,
                expectedPosition: 0,
                cancellationToken: TestContext.Current.CancellationToken));

        // The event just written is the one the boundary check trips over, so its position is
        // known exactly here rather than merely "not -1".
        Assert.Equal(conflictingPosition, ex.ConflictingPosition);
        Assert.Equal(0, ex.ExpectedPosition);
        Assert.Same(dcbQuery, ex.Query);

        // The server's own wording survives into the message — it names which arm of the
        // boundary matched, which the query alone does not say.
        Assert.Contains("event matching types AND tags", ex.Message);
        Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
    }

    [Fact]
    public async Task AppendAsync_WithCorrectExpectedPosition_ShouldSucceed()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());

        var result = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId, 100m), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.Empty
            .WithTypes("order-created")
            .WithTags(tag);

        var result2 = await eventStore.AppendAsync(
            [CreateEvent(new OrderConfirmed(orderId), tag)],
            dcbQuery,
            expectedPosition: result.First().GlobalPosition,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result2);
    }

    #endregion

    #region Regression: single-tag fast path in alberto_read_by_types_and_tags

    /// <summary>
    /// Guards the single-tag fast path (added for one type in migration 029, widened to any
    /// number of types in 030), which drops the <c>DISTINCT</c> the general path relies on.
    /// That is safe only because a single tag matches each position at most once under the
    /// tag PK and an event carries exactly one type — properties of the schema, not of the
    /// query. If either changes, or if the branch is ever widened to multiple tags without
    /// restoring the dedup, this fails.
    /// <para>
    /// The DCB conflict tests above reach this branch too, but they assert only that a
    /// conflict was detected; nothing there would notice a duplicated or missing row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByOneTypeAndOneTag_ReturnsEachMatchOnceInPositionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var orderId = Guid.NewGuid();
        var tag = new EventTag("fastpath-order", orderId.ToString());
        var otherTag = new EventTag("fastpath-order", Guid.NewGuid().ToString());

        var appended = await eventStore.AppendAsync(
            [
                CreateEvent(new OrderCreated(orderId, 100m), tag),   // match
                CreateEvent(new OrderConfirmed(orderId), tag),       // right tag, wrong type
                CreateEvent(new OrderCreated(orderId, 250m), tag),   // match
                CreateEvent(new OrderCreated(orderId, 999m), otherTag), // right type, wrong tag
            ],
            cancellationToken: ct);

        var query = DcbQuery.Empty
            .WithTypes("order-created")
            .WithTags(tag);

        var events = await eventStore.StreamAsync(query, cancellationToken: ct);

        var positions = events.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(2, positions.Length);
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.All(events, e => Assert.Equal("order-created", e.EventType.Id));

        // afterPosition must page the fast path, not restart it.
        var second = await eventStore.StreamAsync(query, afterPosition: positions[0], cancellationToken: ct);
        Assert.Equal([positions[1]], second.Select(e => e.GlobalPosition));

        Assert.Equal(4, appended.Count);
    }

    /// <summary>
    /// The same branch with several types named. Migration 030 matches the type axis with
    /// <c>= ANY</c> here and still emits no <c>DISTINCT</c>; that holds because an event has
    /// exactly one type, so widening the array cannot make a position match twice. A regression
    /// that reintroduced duplication would show up as an event counted more than once — which
    /// on a paged read silently costs a slot of the limit rather than throwing.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByThreeTypesAndOneTag_ReturnsEachMatchOnceInPositionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var orderId = Guid.NewGuid();
        var tag = new EventTag("multitype-order", orderId.ToString());
        var otherTag = new EventTag("multitype-order", Guid.NewGuid().ToString());

        await eventStore.AppendAsync(
            [
                CreateEvent(new OrderCreated(orderId, 100m), tag),        // match
                CreateEvent(new OrderConfirmed(orderId), tag),            // match
                CreateEvent(new OrderCancelled(orderId), tag),            // right tag, unnamed type
                CreateEvent(new OrderCreated(orderId, 250m), tag),        // match
                CreateEvent(new OrderConfirmed(orderId), otherTag),       // named type, wrong tag
            ],
            cancellationToken: ct);

        // Three named types, one of which matches nothing here: the array must filter, not
        // just widen. A fourth name that no event carries also keeps the test honest about
        // ANY over a set larger than the matching set.
        var query = DcbQuery.Empty
            .WithTypes("order-created", "order-confirmed", "order-shipped")
            .WithTags(tag);

        var events = await eventStore.StreamAsync(query, cancellationToken: ct);

        var positions = events.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(3, positions.Length);
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.All(events, e => Assert.Contains(e.EventType.Id, new[] { "order-created", "order-confirmed" }));

        // Paging must resume inside the fast path rather than restart it.
        var second = await eventStore.StreamAsync(query, afterPosition: positions[0], cancellationToken: ct);
        Assert.Equal(positions[1..], second.Select(e => e.GlobalPosition));
    }

    #endregion

    #region Regression: bounded probe per type in alberto_read_by_types

    /// <summary>
    /// Migration 031 replaced <c>event_type = ANY($1)</c> in <c>alberto_read_by_types</c> with one
    /// bounded index probe per named type, merged by a top-N sort. The rewrite is a plan change,
    /// not a semantic one, so what these tests guard is that it stayed one: the same rows, once
    /// each, in position order, honouring <c>afterPosition</c> and <c>limit</c>.
    /// <para>
    /// Every query anchors on the position taken before its own append. The types-only path has
    /// no tag to narrow it, so it would otherwise also return the identically-typed events of
    /// whichever tests ran earlier against this fixture.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByOneType_ReturnsEachMatchOnceInPositionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        await eventStore.AppendAsync(
            [
                CreateEvent(new ProbeAlpha(orderId)),   // match
                CreateEvent(new ProbeBeta(orderId)),    // unnamed type
                CreateEvent(new ProbeAlpha(orderId)),   // match
                CreateEvent(new ProbeGamma(orderId)),   // unnamed type
                CreateEvent(new ProbeAlpha(orderId)),   // match
            ],
            cancellationToken: ct);

        var query = DcbQuery.ByTypes("probe-alpha");

        var events = await eventStore.StreamAsync(query, afterPosition: anchor, cancellationToken: ct);

        var positions = events.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(3, positions.Length);
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Equal(positions.Distinct(), positions);
        Assert.All(events, e => Assert.Equal("probe-alpha", e.EventType.Id));

        // The probe's own LIMIT must page from afterPosition rather than restart at the type's
        // first position.
        var second = await eventStore.StreamAsync(query, afterPosition: positions[0], cancellationToken: ct);
        Assert.Equal(positions[1..], second.Select(e => e.GlobalPosition));

        // The limit applies to the merged result, not per probe.
        var limited = await eventStore.StreamAsync(query, afterPosition: anchor, limit: 2, cancellationToken: ct);
        Assert.Equal(positions[..2], limited.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// Several named types. Each gets its own probe, so this is the case where the merge has to
    /// interleave: the result must be the union in global position order, not one type's run
    /// followed by another's. A type no event carries must contribute nothing rather than widen
    /// the result, and a limit must cut the merged order — the failure mode of a per-probe limit
    /// that forgets to re-limit is a page holding the first <c>limit</c> rows of every type.
    /// </summary>
    [Fact]
    public async Task StreamAsync_BySeveralTypes_ReturnsTheUnionOnceInPositionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        var appended = await eventStore.AppendAsync(
            [
                CreateEvent(new ProbeAlpha(orderId)),   // match
                CreateEvent(new ProbeBeta(orderId)),    // unnamed type
                CreateEvent(new ProbeGamma(orderId)),   // match
                CreateEvent(new ProbeAlpha(orderId)),   // match
                CreateEvent(new ProbeGamma(orderId)),   // match
            ],
            cancellationToken: ct);

        // The types of the appended events, in append order — index 1 is the beta that must not
        // come back.
        var expected = appended
            .Where((_, i) => i != 1)
            .Select(e => e.GlobalPosition)
            .ToArray();

        // "probe-delta" is carried by no event: the array must filter, not merely widen.
        var query = DcbQuery.ByTypes("probe-alpha", "probe-gamma", "probe-delta");

        var events = await eventStore.StreamAsync(query, afterPosition: anchor, cancellationToken: ct);

        var positions = events.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(expected, positions);
        Assert.All(events, e => Assert.Contains(e.EventType.Id, new[] { "probe-alpha", "probe-gamma" }));

        // A limit smaller than either probe's own yield: the answer is the merged prefix, which
        // here alternates between the two types.
        var limited = await eventStore.StreamAsync(query, afterPosition: anchor, limit: 3, cancellationToken: ct);
        Assert.Equal(expected[..3], limited.Select(e => e.GlobalPosition));

        // Paging resumes inside the merge.
        var second = await eventStore.StreamAsync(query, afterPosition: expected[1], cancellationToken: ct);
        Assert.Equal(expected[2..], second.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// The dedup guard. <see cref="DcbQuery"/> concatenates types without deduplicating, so
    /// <c>ByTypes("x").WithTypes("x")</c> reaches the function as <c>{x,x}</c>. Under
    /// <c>= ANY</c> that was harmless — a row either satisfied the predicate or did not, however
    /// many times the value appeared — but one probe per array element would run the same probe
    /// twice and return every position twice. Migration 031 deduplicates the probe source, and
    /// this is what notices if that ever comes out: not an exception, but a page silently spending
    /// half its limit on duplicates.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByTheSameTypeTwice_ReturnsEachMatchOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        await eventStore.AppendAsync(
            [
                CreateEvent(new ProbeAlpha(orderId)),
                CreateEvent(new ProbeBeta(orderId)),
                CreateEvent(new ProbeAlpha(orderId)),
                CreateEvent(new ProbeAlpha(orderId)),
            ],
            cancellationToken: ct);

        var once = await eventStore.StreamAsync(
            DcbQuery.ByTypes("probe-alpha"), afterPosition: anchor, cancellationToken: ct);
        var twice = await eventStore.StreamAsync(
            DcbQuery.ByTypes("probe-alpha").WithTypes("probe-alpha"), afterPosition: anchor, cancellationToken: ct);

        Assert.Equal(
            once.Select(e => e.GlobalPosition),
            twice.Select(e => e.GlobalPosition));

        // And a limited page must not be half duplicates.
        var limited = await eventStore.StreamAsync(
            DcbQuery.ByTypes("probe-alpha").WithTypes("probe-alpha"),
            afterPosition: anchor,
            limit: 2,
            cancellationToken: ct);
        var positions = limited.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(2, positions.Length);
        Assert.Equal(positions.Distinct(), positions);
    }

    /// <summary>
    /// A type no event carries. This is the case the shipped body was slowest on and the one the
    /// rejected alternative — testing <c>event_type</c> on the events row — degraded worst on, so
    /// it is worth an explicit assertion that it returns nothing rather than everything.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByAbsentType_ReturnsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        await eventStore.AppendAsync(
            [CreateEvent(new ProbeAlpha(Guid.NewGuid()))],
            cancellationToken: ct);

        var events = await eventStore.StreamAsync(
            DcbQuery.ByTypes("probe-no-such-type"), cancellationToken: ct);

        Assert.Empty(events);
    }

    #endregion

    #region Regression: bounded tag-axis reads in the all-tags and union functions

    /// <summary>
    /// AND semantics over several tags, the shape migration 033 rewrote from a
    /// <c>GROUP BY … HAVING COUNT(DISTINCT tag) = array_length(p_tags, 1)</c> over every matching
    /// row into one bounded probe on a driving tag with the remaining tags tested by
    /// <c>EXISTS</c>. The plan changed completely; the answer must not have. An event carrying a
    /// superset of the named tags still matches, an event missing any one of them still does not,
    /// and both <c>afterPosition</c> and <c>limit</c> still apply to the merged order.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByAllOfSeveralTags_ReturnsOnlyEventsCarryingEveryTag()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var order = new EventTag("alltags-order", Guid.NewGuid().ToString());
        var region = new EventTag("alltags-region", Guid.NewGuid().ToString());
        var channel = new EventTag("alltags-channel", Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        var appended = await eventStore.AppendAsync(
            [
                CreateTagged(new OrderCreated(orderId, 100m), order, region),           // match
                CreateTagged(new OrderCreated(orderId, 200m), order),                   // missing region
                CreateTagged(new OrderConfirmed(orderId), order, region, channel),      // match, superset
                CreateTagged(new OrderCreated(orderId, 300m), region),                  // missing order
                CreateTagged(new OrderCancelled(orderId), order, region),               // match
            ],
            cancellationToken: ct);

        var expected = appended
            .Where((_, i) => i is 0 or 2 or 4)
            .Select(e => e.GlobalPosition)
            .ToArray();

        var query = DcbQuery.ByAllTags(order, region);

        var events = await eventStore.StreamAsync(query, afterPosition: anchor, cancellationToken: ct);

        var positions = events.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(expected, positions);
        Assert.Equal(positions.Distinct(), positions);

        // The limit cuts the matched order, not the driving tag's raw run — the driving scan sees
        // five candidates here and must stop after two that survive the EXISTS tests.
        var limited = await eventStore.StreamAsync(query, afterPosition: anchor, limit: 2, cancellationToken: ct);
        Assert.Equal(expected[..2], limited.Select(e => e.GlobalPosition));

        // Paging resumes inside the driving scan rather than restarting it.
        var second = await eventStore.StreamAsync(query, afterPosition: expected[0], cancellationToken: ct);
        Assert.Equal(expected[1..], second.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// The driver-selection guard. Migration 033 picks which tag drives the scan at runtime,
    /// because a pooled connection gets a generic plan and the planner therefore never sees the
    /// tag values. That choice is a performance decision and must stay invisible in the result:
    /// naming the same tags in the other order has to return exactly the same rows. Measured on a
    /// million-event store the two orders differ by 20x in time and must differ by nothing here.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByAllTags_IsIndependentOfTheOrderTheTagsAreNamedIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        // Deliberately lopsided: "broad" is carried by every event appended here, "narrow" by two
        // of them. Whichever tag the picker lands on, the answer is the same two events.
        var broad = new EventTag("driver-broad", Guid.NewGuid().ToString());
        var narrow = new EventTag("driver-narrow", Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        var appended = await eventStore.AppendAsync(
            [
                CreateTagged(new OrderCreated(orderId, 100m), broad),
                CreateTagged(new OrderCreated(orderId, 200m), broad, narrow),   // match
                CreateTagged(new OrderConfirmed(orderId), broad),
                CreateTagged(new OrderCancelled(orderId), broad, narrow),       // match
                CreateTagged(new OrderCreated(orderId, 300m), broad),
            ],
            cancellationToken: ct);

        var expected = appended
            .Where((_, i) => i is 1 or 3)
            .Select(e => e.GlobalPosition)
            .ToArray();

        var broadFirst = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(broad, narrow), afterPosition: anchor, cancellationToken: ct);
        var narrowFirst = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(narrow, broad), afterPosition: anchor, cancellationToken: ct);

        Assert.Equal(expected, broadFirst.Select(e => e.GlobalPosition));
        Assert.Equal(expected, narrowFirst.Select(e => e.GlobalPosition));

        // A limit small enough to stop the driving scan early must also be order-independent.
        var broadLimited = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(broad, narrow), afterPosition: anchor, limit: 1, cancellationToken: ct);
        var narrowLimited = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(narrow, broad), afterPosition: anchor, limit: 1, cancellationToken: ct);

        Assert.Equal(expected[..1], broadLimited.Select(e => e.GlobalPosition));
        Assert.Equal(expected[..1], narrowLimited.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// A behaviour change, asserted so it is not mistaken for one. <see cref="DcbQuery"/>
    /// concatenates tags without deduplicating, so <c>ByAllTags(t).WithTags(t)</c> reaches the
    /// function as <c>{t,t}</c>. The body migration 033 replaced compared
    /// <c>COUNT(DISTINCT tag)</c> — one — against <c>array_length(p_tags, 1)</c> — two — and so
    /// returned <em>nothing</em> for a query that plainly should match every event tagged
    /// <c>t</c>. The rewrite removes every occurrence of the driving tag before testing the rest,
    /// so the duplicate collapses and the query answers the same as naming the tag once.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByTheSameTagTwice_MatchesEventsCarryingItRatherThanNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var order = new EventTag("dup-tag-order", Guid.NewGuid().ToString());
        var other = new EventTag("dup-tag-other", Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        await eventStore.AppendAsync(
            [
                CreateTagged(new OrderCreated(orderId, 100m), order),
                CreateTagged(new OrderConfirmed(orderId), other),
                CreateTagged(new OrderCancelled(orderId), order, other),
            ],
            cancellationToken: ct);

        var once = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(order), afterPosition: anchor, cancellationToken: ct);
        var twice = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(order).WithTags(order), afterPosition: anchor, cancellationToken: ct);

        Assert.Equal(2, once.Count);
        Assert.Equal(
            once.Select(e => e.GlobalPosition),
            twice.Select(e => e.GlobalPosition));

        // Repeating one of two distinct tags must not change the conjunction either.
        var bothOnce = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(order, other), afterPosition: anchor, cancellationToken: ct);
        var bothWithDuplicate = await eventStore.StreamAsync(
            DcbQuery.ByAllTags(order, other).WithTags(order), afterPosition: anchor, cancellationToken: ct);

        Assert.Single(bothOnce);
        Assert.Equal(
            bothOnce.Select(e => e.GlobalPosition),
            bothWithDuplicate.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// A tag no event carries. Under AND semantics one absent tag empties the whole result, and
    /// it is also the case the runtime driver picker should choose: a tag that runs out before
    /// the probe's cap wins outright, which here means the driving scan finds nothing and stops
    /// rather than walking the other tag's rows to reject each one.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByAllTagsIncludingAnAbsentTag_ReturnsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var order = new EventTag("absent-tag-order", Guid.NewGuid().ToString());
        var absent = new EventTag("absent-tag-none", Guid.NewGuid().ToString());

        await eventStore.AppendAsync(
            [CreateTagged(new OrderCreated(Guid.NewGuid(), 100m), order)],
            cancellationToken: ct);

        Assert.Empty(await eventStore.StreamAsync(
            DcbQuery.ByAllTags(order, absent), cancellationToken: ct));
        Assert.Empty(await eventStore.StreamAsync(
            DcbQuery.ByAllTags(absent, order), cancellationToken: ct));
    }

    /// <summary>
    /// All tags AND a single named type. Migration 033 keeps migration 030's branch here: at
    /// exactly one type the type axis is a scalar probe into the type-position index, and from
    /// two types upward it is a test on the events row. This covers the first branch; the test
    /// below covers the second. Both must agree with each other and with the tag-only shape.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByOneTypeAndAllTags_ReturnsOnlyEventsMatchingBothAxes()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var order = new EventTag("tat-one-order", Guid.NewGuid().ToString());
        var region = new EventTag("tat-one-region", Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        var appended = await eventStore.AppendAsync(
            [
                CreateTagged(new OrderCreated(orderId, 100m), order, region),   // match
                CreateTagged(new OrderConfirmed(orderId), order, region),       // both tags, wrong type
                CreateTagged(new OrderCreated(orderId, 200m), order),           // right type, missing region
                CreateTagged(new OrderCreated(orderId, 300m), order, region),   // match
            ],
            cancellationToken: ct);

        var expected = appended
            .Where((_, i) => i is 0 or 3)
            .Select(e => e.GlobalPosition)
            .ToArray();

        var query = DcbQuery.ByAllTags(order, region).WithTypes("order-created");

        var events = await eventStore.StreamAsync(query, afterPosition: anchor, cancellationToken: ct);

        Assert.Equal(expected, events.Select(e => e.GlobalPosition));
        Assert.All(events, e => Assert.Equal("order-created", e.EventType.Id));

        var limited = await eventStore.StreamAsync(query, afterPosition: anchor, limit: 1, cancellationToken: ct);
        Assert.Equal(expected[..1], limited.Select(e => e.GlobalPosition));

        var second = await eventStore.StreamAsync(query, afterPosition: expected[0], cancellationToken: ct);
        Assert.Equal(expected[1..], second.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// All tags AND several named types — the branch that tests <c>event_type</c> on the events
    /// row. That is only safe while the driving tag scan bounds how many events rows are ever
    /// considered, so the assertion that matters is the same one as everywhere else: the array
    /// filters rather than widens, including when it names a type no event carries.
    /// </summary>
    [Fact]
    public async Task StreamAsync_BySeveralTypesAndAllTags_ReturnsOnlyEventsMatchingBothAxes()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var order = new EventTag("tat-many-order", Guid.NewGuid().ToString());
        var region = new EventTag("tat-many-region", Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        var appended = await eventStore.AppendAsync(
            [
                CreateTagged(new OrderCreated(orderId, 100m), order, region),   // match
                CreateTagged(new OrderCancelled(orderId), order, region),       // both tags, unnamed type
                CreateTagged(new OrderConfirmed(orderId), order, region),       // match
                CreateTagged(new OrderConfirmed(orderId), order),               // named type, missing region
                CreateTagged(new OrderCreated(orderId, 200m), order, region),   // match
            ],
            cancellationToken: ct);

        var expected = appended
            .Where((_, i) => i is 0 or 2 or 4)
            .Select(e => e.GlobalPosition)
            .ToArray();

        // "order-shipped" is carried by no event here.
        var query = DcbQuery.ByAllTags(order, region)
            .WithTypes("order-created", "order-confirmed", "order-shipped");

        var events = await eventStore.StreamAsync(query, afterPosition: anchor, cancellationToken: ct);

        Assert.Equal(expected, events.Select(e => e.GlobalPosition));
        Assert.All(events, e => Assert.Contains(e.EventType.Id, new[] { "order-created", "order-confirmed" }));

        var limited = await eventStore.StreamAsync(query, afterPosition: anchor, limit: 2, cancellationToken: ct);
        Assert.Equal(expected[..2], limited.Select(e => e.GlobalPosition));

        var second = await eventStore.StreamAsync(query, afterPosition: expected[1], cancellationToken: ct);
        Assert.Equal(expected[2..], second.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// The union shape with ANY-tag semantics, reached through <see cref="DcbQuery.AsUnion"/>.
    /// Migration 033 replaced an unbounded <c>UNION</c> of two <c>= ANY</c> scans with a
    /// parenthesised union of per-value bounded probes, and the parentheses are the subtle part:
    /// a trailing <c>ORDER BY … LIMIT</c> after a <c>UNION</c> binds to the whole union, which
    /// would leave one arm unbounded and the fix silently half-applied. What that costs is
    /// performance, not rows, so what this test actually guards is the rest: an event matching
    /// both arms appears once, and a limit cuts the merged order rather than each arm separately.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByTypesOrTags_ReturnsTheUnionOnceInPositionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var order = new EventTag("or-tags-order", Guid.NewGuid().ToString());
        var other = new EventTag("or-tags-other", Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        var appended = await eventStore.AppendAsync(
            [
                CreateTagged(new UnionAlpha(orderId)),                  // type arm
                CreateTagged(new OrderCreated(orderId, 100m), order),   // tag arm
                CreateTagged(new UnionBeta(orderId), other),            // neither
                CreateTagged(new UnionAlpha(orderId), order),           // both arms — must appear once
                CreateTagged(new OrderConfirmed(orderId), other),       // neither
            ],
            cancellationToken: ct);

        var expected = appended
            .Where((_, i) => i is 0 or 1 or 3)
            .Select(e => e.GlobalPosition)
            .ToArray();

        var query = DcbQuery.ByTags(order).WithTypes("union-alpha").AsUnion();

        var events = await eventStore.StreamAsync(query, afterPosition: anchor, cancellationToken: ct);

        var positions = events.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(expected, positions);
        Assert.Equal(positions.Distinct(), positions);

        // Both arms yield rows before this limit; the answer is the merged prefix, which
        // interleaves them.
        var limited = await eventStore.StreamAsync(query, afterPosition: anchor, limit: 2, cancellationToken: ct);
        Assert.Equal(expected[..2], limited.Select(e => e.GlobalPosition));

        var second = await eventStore.StreamAsync(query, afterPosition: expected[0], cancellationToken: ct);
        Assert.Equal(expected[1..], second.Select(e => e.GlobalPosition));
    }

    /// <summary>
    /// The union shape with ALL-tag semantics: match any named type, or carry every named tag.
    /// This was the slowest of the four — a <c>SELECT DISTINCT</c> over whole event rows, two
    /// jsonb columns included — and it is the one place both rewrites meet, since its tag arm is
    /// the driver-and-<c>EXISTS</c> scan and its type arm is the bounded per-type probe.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ByTypesOrAllTags_ReturnsTheUnionOnceInPositionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var order = new EventTag("or-all-order", Guid.NewGuid().ToString());
        var region = new EventTag("or-all-region", Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();

        var anchor = await eventStore.GetLastPositionAsync(ct);
        var appended = await eventStore.AppendAsync(
            [
                CreateTagged(new UnionGamma(orderId)),                          // type arm
                CreateTagged(new OrderCreated(orderId, 100m), order, region),   // tag arm
                CreateTagged(new OrderConfirmed(orderId), order),               // one tag short of the arm
                CreateTagged(new UnionGamma(orderId), order, region),           // both arms — once
                CreateTagged(new OrderCancelled(orderId), region),              // one tag short of the arm
            ],
            cancellationToken: ct);

        var expected = appended
            .Where((_, i) => i is 0 or 1 or 3)
            .Select(e => e.GlobalPosition)
            .ToArray();

        var query = DcbQuery.ByAllTags(order, region).WithTypes("union-gamma").AsUnion();

        var events = await eventStore.StreamAsync(query, afterPosition: anchor, cancellationToken: ct);

        var positions = events.Select(e => e.GlobalPosition).ToArray();
        Assert.Equal(expected, positions);
        Assert.Equal(positions.Distinct(), positions);

        var limited = await eventStore.StreamAsync(query, afterPosition: anchor, limit: 2, cancellationToken: ct);
        Assert.Equal(expected[..2], limited.Select(e => e.GlobalPosition));

        var second = await eventStore.StreamAsync(query, afterPosition: expected[0], cancellationToken: ct);
        Assert.Equal(expected[1..], second.Select(e => e.GlobalPosition));
    }

    #endregion

    #region GetLastPosition Tests

    [Fact]
    public async Task GetLastPositionAsync_ShouldReturnPositionAfterAppend()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var result = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var position = await eventStore.GetLastPositionAsync(
            TestContext.Current.CancellationToken);

        Assert.True(position >= result.First().GlobalPosition);
    }

    #endregion

    #region StreamAllAsync Tests

    [Fact]
    public async Task StreamAllAsync_ShouldReturnAllEvents()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var startPosition = await eventStore.GetLastPositionAsync(TestContext.Current.CancellationToken);

        var result1 = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var result2 = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 200m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var events = await eventStore.StreamAllAsync(
            afterPosition: startPosition,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(events.Count >= 2);
    }

    #endregion

    #region Helper Methods

    private static EventToPersist CreateEvent<TEvent>(TEvent @event, EventTag? tag = null)
        where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = tag.HasValue ? [tag.Value] : [],
            EventData = JsonSerializer.Serialize(@event)
        };
    }

    /// <summary>
    /// The multi-tag counterpart of <see cref="CreateEvent"/>. Separate rather than an overload
    /// so that <c>CreateEvent(e, tag)</c> keeps resolving to the single-tag helper unchanged.
    /// </summary>
    private static EventToPersist CreateTagged<TEvent>(TEvent @event, params EventTag[] tags)
        where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = [..tags],
            EventData = JsonSerializer.Serialize(@event)
        };
    }

    #endregion
}
