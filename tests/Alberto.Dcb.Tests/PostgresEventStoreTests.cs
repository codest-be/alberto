using System.Text.Json;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Integration tests for PostgresEventStore with inline projection support.
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

#pragma warning disable CS0618 // Testing with deprecated Projection<T> API
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
#pragma warning restore CS0618

    #endregion

    #region Basic Append Tests

    [Fact]
    public async Task AppendAsync_ShouldPersistEvents()
    {
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var stateStore = new PostgresStateStore<OrderSummary>(
            fixture.DataSource, "OrderSummaryProjection-" + tenantId);
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));

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

    #region GetLastPosition Tests

    [Fact]
    public async Task GetLastPositionAsync_ShouldReturnPositionAfterAppend()
    {
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));

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
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));

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
