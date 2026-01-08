using System.Text.Json;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Integration tests for PostgresEventStore with inline projection support.
/// Verifies that events are persisted and projections run immediately after.
/// </summary>
public sealed class PostgresEventStoreTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

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

    public class OrderSummaryProjection : Projection<OrderSummary>,
        IProject<OrderSummary, OrderCreated>,
        IProject<OrderSummary, OrderConfirmed>
    {
        public string GetDocumentId(OrderCreated @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCreated @event, ProjectionContext context)
            => new OrderSummary { OrderId = @event.OrderId, Amount = @event.Amount, Status = "Created" };

        public string GetDocumentId(OrderConfirmed @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderConfirmed @event, ProjectionContext context)
            => state with { Status = "Confirmed" };
    }

    #endregion

    #region Basic Append Tests

    [Fact]
    public async Task AppendAsync_ShouldPersistEvents()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var orderId = Guid.NewGuid();
        var result = await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(orderId, 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(orderId, JsonSerializer.Deserialize<OrderCreated>(result.First().EventData)!.OrderId);
    }

    [Fact]
    public async Task AppendAsync_ShouldReturnGlobalPosition()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var result1 = await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var result2 = await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 200m))],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result2.First().GlobalPosition > result1.First().GlobalPosition);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnAppendedEvents()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var orderId = Guid.NewGuid();
        await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(orderId, 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var events = await eventStore.StreamAsync(
            tenantId,
            DcbQuery.Empty,
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
        var eventStore = new PostgresEventStore(_fixture.DataSource);
        var stateStore = new PostgresStateStore<OrderSummary>(
            _fixture.DataSource, tenantId, "OrderSummaryProjection");
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();
        await eventStore.AppendAsync(
            tenantId,
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
        var eventStore = new PostgresEventStore(_fixture.DataSource);
        var stateStore = new PostgresStateStore<OrderSummary>(
            _fixture.DataSource, tenantId, "OrderSummaryProjection");
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(orderId, 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        await eventStore.AppendAsync(
            tenantId,
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
        var eventStore = new PostgresEventStore(_fixture.DataSource);
        var stateStore = new PostgresStateStore<OrderSummary>(
            _fixture.DataSource, tenantId, "OrderSummaryProjection");
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync(
            tenantId,
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
        var eventStore = new PostgresEventStore(_fixture.DataSource);
        var stateStore = new PostgresStateStore<OrderSummary>(
            _fixture.DataSource, tenantId, "OrderSummaryProjection");
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        // OrderCancelled is not handled by OrderSummaryProjection
        await eventStore.AppendAsync(
            tenantId,
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
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());

        // First append succeeds
        await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(orderId, 100m), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        // Second append with DCB conflict check
        var dcbQuery = DcbQuery.Empty
            .WithTypes("order-created")
            .WithTags(tag);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            eventStore.AppendAsync(
                tenantId,
                [CreateEvent(new OrderConfirmed(orderId), tag)],
                dcbQuery,
                expectedPosition: 0,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_WithCorrectExpectedPosition_ShouldSucceed()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());

        // First append
        var result = await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(orderId, 100m), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        // Second append with correct expected position
        var dcbQuery = DcbQuery.Empty
            .WithTypes("order-created")
            .WithTags(tag);

        var result2 = await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderConfirmed(orderId), tag)],
            dcbQuery,
            expectedPosition: result.First().GlobalPosition,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result2);
    }

    #endregion

    #region GetLastPosition Tests

    [Fact]
    public async Task GetLastPositionAsync_WithNoEvents_ShouldReturnZero()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var position = await eventStore.GetLastPositionAsync(
            tenantId,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, position);
    }

    [Fact]
    public async Task GetLastPositionAsync_ShouldReturnLatestPosition()
    {
        var tenantId = Guid.NewGuid().ToString();
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var result = await eventStore.AppendAsync(
            tenantId,
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var position = await eventStore.GetLastPositionAsync(
            tenantId,
            TestContext.Current.CancellationToken);

        Assert.Equal(result.First().GlobalPosition, position);
    }

    [Fact]
    public async Task GetLastPositionGlobalAsync_ShouldReturnGlobalPosition()
    {
        var eventStore = new PostgresEventStore(_fixture.DataSource);

        var result = await eventStore.AppendAsync(
            Guid.NewGuid().ToString(),
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var position = await eventStore.GetLastPositionGlobalAsync(
            TestContext.Current.CancellationToken);

        Assert.True(position >= result.First().GlobalPosition);
    }

    #endregion

    #region StreamGlobalAsync Tests

    [Fact]
    public async Task StreamGlobalAsync_ShouldReturnEventsAcrossTenants()
    {
        var eventStore = new PostgresEventStore(_fixture.DataSource);
        var tenant1 = Guid.NewGuid().ToString();
        var tenant2 = Guid.NewGuid().ToString();

        var result1 = await eventStore.AppendAsync(
            tenant1,
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var result2 = await eventStore.AppendAsync(
            tenant2,
            [CreateEvent(new OrderCreated(Guid.NewGuid(), 200m))],
            cancellationToken: TestContext.Current.CancellationToken);

        var minPosition = Math.Min(
            result1.First().GlobalPosition,
            result2.First().GlobalPosition) - 1;

        var events = await eventStore.StreamGlobalAsync(
            afterPosition: minPosition,
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
            TenantId = "test",
            EventType = new EventType(eventTypeId),
            Tags = tag.HasValue ? [tag.Value] : [],
            EventData = JsonSerializer.Serialize(@event)
        };
    }

    #endregion
}
