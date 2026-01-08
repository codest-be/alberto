using System.Data;
using System.Text.Json;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for the InlineProjection wrapper class.
/// </summary>
public class InlineProjectionTests
{
    #region Test Events

    [EventType("order-created")]
    public record OrderCreated(Guid OrderId, decimal Amount) : IEvent;

    [EventType("order-confirmed")]
    public record OrderConfirmed(Guid OrderId) : IEvent;

    [EventType("order-cancelled")]
    public record OrderCancelled(Guid OrderId) : IEvent;

    #endregion

    #region Test State

    public record OrderSummary
    {
        public Guid OrderId { get; init; }
        public decimal Amount { get; init; }
        public string Status { get; init; } = "";
    }

    #endregion

    #region Test Projection

    public class OrderSummaryProjection : Projection<OrderSummary>,
        IProject<OrderSummary, OrderCreated>,
        IProject<OrderSummary, OrderConfirmed>,
        IProject<OrderSummary, OrderCancelled>
    {
        public string GetDocumentId(OrderCreated @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCreated @event, ProjectionContext context)
            => new OrderSummary { OrderId = @event.OrderId, Amount = @event.Amount, Status = "Created" };

        public string GetDocumentId(OrderConfirmed @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderConfirmed @event, ProjectionContext context)
            => state with { Status = "Confirmed" };

        public string GetDocumentId(OrderCancelled @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCancelled @event, ProjectionContext context)
            => ProjectionResults.Delete<OrderSummary>();
    }

    #endregion

    #region In-Memory State Store

    private class InMemoryStateStore : IStateStore<OrderSummary>
    {
        private readonly Dictionary<string, OrderSummary> _store = new();

        public IReadOnlyDictionary<string, OrderSummary> Store => _store;
        public List<string> DeletedIds { get; } = new();

        public Task<Dictionary<string, OrderSummary>> LoadManyAsync(
            IEnumerable<string> documentIds,
            IDbTransaction? transaction = null,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, OrderSummary>();
            foreach (var id in documentIds)
            {
                if (_store.TryGetValue(id, out var state))
                    result[id] = state;
            }
            return Task.FromResult(result);
        }

        public Task ApplyChangesAsync(
            IReadOnlyDictionary<string, OrderSummary> upserts,
            IReadOnlyCollection<string> deletes,
            IDbTransaction? transaction = null,
            CancellationToken ct = default)
        {
            foreach (var (id, state) in upserts)
            {
                _store[id] = state;
            }

            foreach (var id in deletes)
            {
                _store.Remove(id);
                DeletedIds.Add(id);
            }

            return Task.CompletedTask;
        }
    }

    #endregion

    #region Tests

    [Fact]
    public void HandledEventTypes_ShouldContainProjectionTypes()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        Assert.Contains("order-created", inline.HandledEventTypes);
        Assert.Contains("order-confirmed", inline.HandledEventTypes);
        Assert.Contains("order-cancelled", inline.HandledEventTypes);
    }

    [Fact]
    public void Projection_ShouldBeAccessible()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        Assert.NotNull(inline.Projection);
        Assert.IsType<OrderSummaryProjection>(inline.Projection);
    }

    [Fact]
    public async Task ProcessAsync_ShouldUpsertNewState()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();
        var events = new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        };

        await inline.ProcessAsync(events, transaction: null!);

        Assert.Single(stateStore.Store);
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal(orderId, state.OrderId);
        Assert.Equal(100m, state.Amount);
        Assert.Equal("Created", state.Status);
    }

    [Fact]
    public async Task ProcessAsync_ShouldUpdateExistingState()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();

        // First batch: create
        await inline.ProcessAsync(new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        }, transaction: null!);

        // Second batch: confirm
        await inline.ProcessAsync(new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderConfirmed(orderId), 2)
        }, transaction: null!);

        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
        Assert.Equal(100m, state.Amount);
    }

    [Fact]
    public async Task ProcessAsync_ShouldDeleteOnCancellation()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();

        // Create
        await inline.ProcessAsync(new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        }, transaction: null!);

        // Cancel
        await inline.ProcessAsync(new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCancelled(orderId), 2)
        }, transaction: null!);

        Assert.Empty(stateStore.Store);
        Assert.Contains(orderId.ToString(), stateStore.DeletedIds);
    }

    [Fact]
    public async Task ProcessAsync_ShouldHandleMultipleDocuments()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();

        await inline.ProcessAsync(new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(order1, 100m), 1),
            CreateEnvelope(new OrderCreated(order2, 200m), 2),
            CreateEnvelope(new OrderConfirmed(order1), 3)
        }, transaction: null!);

        Assert.Equal(2, stateStore.Store.Count);
        Assert.Equal("Confirmed", stateStore.Store[order1.ToString()].Status);
        Assert.Equal("Created", stateStore.Store[order2.ToString()].Status);
    }

    [Fact]
    public async Task ProcessAsync_ShouldFoldEventsForSameDocument()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        var orderId = Guid.NewGuid();

        // All events for same order in one batch
        await inline.ProcessAsync(new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1),
            CreateEnvelope(new OrderConfirmed(orderId), 2)
        }, transaction: null!);

        // Should have folded to final state
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
    }

    [Fact]
    public async Task ProcessAsync_EmptyBatch_ShouldNotThrow()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        await inline.ProcessAsync([], transaction: null!);

        Assert.Empty(stateStore.Store);
    }

    [Fact]
    public async Task ProcessAsync_UnhandledEventTypes_ShouldBeIgnored()
    {
        var stateStore = new InMemoryStateStore();
        var inline = new InlineProjection<OrderSummary, OrderSummaryProjection>(stateStore);

        // Create an envelope with an unhandled event type
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            GlobalPosition = 1,
            EventType = new EventType("unknown-event"),
            Tags = [],
            EventData = "{}",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };

        await inline.ProcessAsync([envelope], transaction: null!);

        Assert.Empty(stateStore.Store);
    }

    #endregion

    #region Helper Methods

    private static IEventEnvelope CreateEnvelope<TEvent>(TEvent @event, long position) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));

        return new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            GlobalPosition = position,
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };
    }

    #endregion
}
