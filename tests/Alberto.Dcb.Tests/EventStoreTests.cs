using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Behavioral tests for the backend-agnostic <see cref="EventStore"/>.
/// </summary>
public sealed class EventStoreTests
{
    #region Test Events

    [EventType("order-created")]
    public record OrderCreated(Guid OrderId, decimal Amount) : IEvent;

    [EventType("order-confirmed")]
    public record OrderConfirmed(Guid OrderId) : IEvent;

    #endregion

    #region Orchestration Tests

    [Fact]
    public async Task AppendAsync_RunsProjectionBeforePostAppendHandler()
    {
        var calls = new List<string>();
        var projection = new RecordingProjection(calls);
        var handler = new RecordingPostAppendHandler(calls);
        var eventStore = new EventStore(
            new InMemoryEventStoreBackend(),
            [projection],
            [handler]);

        await eventStore.AppendAsync(
            [
                CreateEvent(new OrderCreated(Guid.NewGuid(), 100m)),
                new EventToPersist
                {
                    EventType = new EventType("unrelated-event"),
                    Tags = [],
                    EventData = "{}"
                }
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["projection", "handler"], calls);
        Assert.Single(projection.Received);
        Assert.Single(handler.Received);
        Assert.Equal("order-created", projection.Received[0].EventType.Id);
        Assert.Equal("order-created", handler.Received[0].EventType.Id);
    }

    [Fact]
    public async Task AppendAsync_ProjectionFailure_SkipsPostAppendHandlersButKeepsPersistedEvent()
    {
        var handler = new RecordingPostAppendHandler([]);
        var eventStore = new EventStore(
            new InMemoryEventStoreBackend(),
            [new ThrowingProjection()],
            [handler]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            eventStore.AppendAsync(
                [CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Received);
        var persisted = await eventStore.StreamAllAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(persisted);
    }

    private sealed class RecordingProjection(List<string> calls) : IInlineProjection
    {
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal) { "order-created" };

        public List<IEventEnvelope> Received { get; } = [];

        public Task ProcessAsync(
            IReadOnlyList<IEventEnvelope> events,
            CancellationToken ct = default)
        {
            calls.Add("projection");
            Received.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPostAppendHandler(List<string> calls) : IPostAppendHandler
    {
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal) { "order-created" };

        public List<IEventEnvelope> Received { get; } = [];

        public Task ProcessAsync(
            IReadOnlyList<IEventEnvelope> events,
            CancellationToken ct)
        {
            calls.Add("handler");
            Received.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProjection : IInlineProjection
    {
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal) { "order-created" };

        public Task ProcessAsync(
            IReadOnlyList<IEventEnvelope> events,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("projection failed");
    }

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
            _inner = new DeclaredAsyncProjection<TState>(declaration, () => stateStore);
        }

        public IReadOnlySet<string> HandledEventTypes { get; }

        public Task ProcessAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
            => _inner.ProcessBatchAsync(events, ct);
    }

    #endregion

    #region In-Memory State Store

    private class InMemoryStateStore<TState> : IStateStore<TState>
    {
        private readonly Dictionary<string, TState> _store = new();

        public IReadOnlyDictionary<string, TState> Store => _store;

        public Task<Dictionary<string, TState>> LoadManyAsync(
            IEnumerable<string> documentIds,
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
            IReadOnlyList<TState> result = _store.Values.Reverse().Take(limit).ToList();
            return Task.FromResult(result);
        }
    }

    #endregion

    #region Inline Projection Tests

    [Fact]
    public async Task AppendAsync_ShouldRunInlineProjections()
    {
        var eventStore = new EventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

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
        var eventStore = new EventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

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
        var eventStore = new EventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

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
        var eventStore = new EventStore(new InMemoryEventStoreBackend());
        var stateStore = new InMemoryStateStore<OrderSummary>();
        eventStore.RegisterInlineProjection(
            new InlineStateStoreProjection<OrderSummary>(OrderSummaryDeclaration(), stateStore));

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
        var eventStore = new EventStore(new InMemoryEventStoreBackend());

        var orderId = Guid.NewGuid();
        var result = await eventStore.AppendAsync([CreateEvent(new OrderCreated(orderId, 100m))], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    #endregion

    #region Delegate Tests

    [Fact]
    public async Task StreamAsync_ShouldDelegateToBackend()
    {
        var eventStore = new EventStore(new InMemoryEventStoreBackend());
        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync([CreateEvent(new OrderCreated(orderId, 100m))], cancellationToken: TestContext.Current.CancellationToken);

        var events = await eventStore.StreamAsync(DcbQuery.Empty, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(events);
        Assert.Equal("order-created", events.First().EventType.Id);
    }

    [Fact]
    public async Task StreamAllAsync_ShouldDelegateToBackend()
    {
        var eventStore = new EventStore(new InMemoryEventStoreBackend());

        await eventStore.AppendAsync([CreateEvent(new OrderCreated(Guid.NewGuid(), 100m))], cancellationToken: TestContext.Current.CancellationToken);
        await eventStore.AppendAsync([CreateEvent(new OrderCreated(Guid.NewGuid(), 200m))], cancellationToken: TestContext.Current.CancellationToken);

        var events = await eventStore.StreamAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task GetLastPositionAsync_ShouldDelegateToBackend()
    {
        var eventStore = new EventStore(new InMemoryEventStoreBackend());

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
