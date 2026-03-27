using System.Data;
using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Tests for InMemoryEventStore, focusing on inline projection support.
/// </summary>
public sealed class InMemoryEventStoreTests
{
    #region Test Events

    [EventType("order-created")]
    public record OrderCreated(Guid OrderId, decimal Amount) : IEvent;

    [EventType("order-confirmed")]
    public record OrderConfirmed(Guid OrderId) : IEvent;

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

    #region In-Memory State Store

    private class InMemoryStateStore<TState> : IStateStore<TState>
    {
        private readonly Dictionary<string, TState> _store = new();

        public IReadOnlyDictionary<string, TState> Store => _store;

        public Task<Dictionary<string, TState>> LoadManyAsync(
            IEnumerable<string> documentIds,
            IDbTransaction? transaction = null,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, TState>();
            foreach (var id in documentIds)
            {
                if (_store.TryGetValue(id, out var state))
                    result[id] = state;
            }
            return Task.FromResult(result);
        }

        public Task ApplyChangesAsync(
            IReadOnlyDictionary<string, TState> upserts,
            IReadOnlyCollection<string> deletes,
            IDbTransaction? transaction = null,
            CancellationToken ct = default)
        {
            foreach (var (id, state) in upserts)
                _store[id] = state;

            foreach (var id in deletes)
                _store.Remove(id);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TState>> ListRecentAsync(
            int limit = 20,
            CancellationToken ct = default)
        {
            IReadOnlyList<TState> result = _store.Values.Take(limit).ToList();
            return Task.FromResult(result);
        }
    }

    #endregion

    #region Inline Projection Tests

    [Fact]
    public async Task AppendAsync_ShouldRunInlineProjections()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();
        await eventStore.AppendAsync([CreateEvent(new OrderCreated(orderId, 100m))], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(stateStore.Store);
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal(orderId, state.OrderId);
        Assert.Equal(100m, state.Amount);
        Assert.Equal("Created", state.Status);
    }

    [Fact]
    public async Task AppendAsync_ShouldUpdateProjectionWithSubsequentEvents()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync([CreateEvent(new OrderCreated(orderId, 100m))], cancellationToken: TestContext.Current.CancellationToken);
        await eventStore.AppendAsync([CreateEvent(new OrderConfirmed(orderId))], cancellationToken: TestContext.Current.CancellationToken);

        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
        Assert.Equal(100m, state.Amount);
    }

    [Fact]
    public async Task AppendAsync_ShouldHandleMultipleEventsInSingleAppend()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync([
                CreateEvent(new OrderCreated(orderId, 100m)),
                CreateEvent(new OrderConfirmed(orderId))
            ], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(stateStore.Store);
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
    }

    [Fact]
    public async Task AppendAsync_ShouldNotRunProjectionsWhenNoRelevantEvents()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        await eventStore.AppendAsync([new EventToPersist
            {
                EventType = new EventType("unrelated-event"),
                Tags = [],
                EventData = "{}"
            }], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
    }

    [Fact]
    public async Task AppendAsync_WithoutProjections_ShouldStillWork()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());

        var orderId = Guid.NewGuid();
        var result = await eventStore.AppendAsync([CreateEvent(new OrderCreated(orderId, 100m))], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    #endregion

    #region Delegate Tests

    [Fact]
    public async Task StreamAsync_ShouldDelegateToBackend()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());
        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync([CreateEvent(new OrderCreated(orderId, 100m))], cancellationToken: TestContext.Current.CancellationToken);

        var events = await eventStore.StreamAsync(DcbQuery.Empty, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(events);
        Assert.Equal("order-created", events.First().EventType.Id);
    }

    [Fact]
    public async Task StreamAllAsync_ShouldDelegateToBackend()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());

        await eventStore.AppendAsync([CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))], cancellationToken: TestContext.Current.CancellationToken);
        await eventStore.AppendAsync([CreateEvent(new OrderCreated(Guid.NewGuid(), 200m))], cancellationToken: TestContext.Current.CancellationToken);

        var events = await eventStore.StreamAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task GetLastPositionAsync_ShouldDelegateToBackend()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreBackend());

        await eventStore.AppendAsync([CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))], cancellationToken: TestContext.Current.CancellationToken);
        await eventStore.AppendAsync([CreateEvent(new OrderCreated(Guid.NewGuid(), 200m))], cancellationToken: TestContext.Current.CancellationToken);

        var position = await eventStore.GetLastPositionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, position);
    }

    #endregion

    #region Helper Methods

    private static EventToPersist CreateEvent<TEvent>(TEvent @event) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event)
        };
    }

    #endregion
}
