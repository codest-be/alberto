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

    [Fact]
    public async Task AppendAsync_WithDcbConflict_ShouldThrowDcbConflictException()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());

        await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId, 100m), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.Empty
            .WithTypes("order-created")
            .WithTags(tag);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            eventStore.AppendAsync(
                [CreateEvent(new OrderConfirmed(orderId), tag)],
                dcbQuery,
                expectedPosition: 0,
                cancellationToken: TestContext.Current.CancellationToken));
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

    #endregion
}
