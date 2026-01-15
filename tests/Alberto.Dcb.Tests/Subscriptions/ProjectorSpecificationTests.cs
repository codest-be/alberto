using System.Data;
using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for async projection processing via consumer registration.
/// </summary>
public class ProjectorSpecificationTests
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

        public Task<IReadOnlyList<OrderSummary>> ListRecentAsync(
            int limit = 20,
            CancellationToken ct = default)
        {
            IReadOnlyList<OrderSummary> result = _store.Values.Take(limit).ToList();
            return Task.FromResult(result);
        }
    }

    #endregion

    #region ProcessEvent Tests

    private static (IEventProcessor processor, InMemoryStateStore stateStore) CreateProcessor()
    {
        var stateStore = new InMemoryStateStore();
        var processor = new AsyncProjection<OrderSummary, OrderSummaryProjection>(
            stateStore, "order-summary-v1");
        return (processor, stateStore);
    }

    [Fact]
    public async Task ProcessEvent_ShouldUpsertState()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderCreated(orderId, 100m), 1);

        await processor.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Single(stateStore.Store);
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal(orderId, state.OrderId);
        Assert.Equal(100m, state.Amount);
        Assert.Equal("Created", state.Status);
    }

    [Fact]
    public async Task ProcessEvent_ShouldUpdateExistingState()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // First event: create
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCreated(orderId, 100m), 1),
            TestContext.Current.CancellationToken);

        // Second event: confirm
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderConfirmed(orderId), 2),
            TestContext.Current.CancellationToken);

        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
        Assert.Equal(100m, state.Amount); // Preserved
    }

    [Fact]
    public async Task ProcessEvent_ShouldDeleteOnCancellation()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // Create
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCreated(orderId, 100m), 1),
            TestContext.Current.CancellationToken);

        // Cancel
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCancelled(orderId), 2),
            TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
        Assert.Contains(orderId.ToString(), stateStore.DeletedIds);
    }

    [Fact]
    public async Task ProcessEvent_ShouldHandleMultipleDocuments()
    {
        var (processor, stateStore) = CreateProcessor();

        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();

        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCreated(order1, 100m), 1),
            TestContext.Current.CancellationToken);
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCreated(order2, 200m), 2),
            TestContext.Current.CancellationToken);
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderConfirmed(order1), 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, stateStore.Store.Count);
        Assert.Equal("Confirmed", stateStore.Store[order1.ToString()].Status);
        Assert.Equal("Created", stateStore.Store[order2.ToString()].Status);
    }

    [Fact]
    public async Task ProcessEvent_ShouldFoldEventsForSameDocument()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // Events for same order processed sequentially
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCreated(orderId, 100m), 1),
            TestContext.Current.CancellationToken);
        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderConfirmed(orderId), 2),
            TestContext.Current.CancellationToken);

        // Should have folded to final state
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
    }

    [Fact]
    public void HandledEventTypes_ShouldContainProjectionTypes()
    {
        var (processor, _) = CreateProcessor();

        Assert.Contains("order-created", processor.HandledEventTypes);
        Assert.Contains("order-confirmed", processor.HandledEventTypes);
        Assert.Contains("order-cancelled", processor.HandledEventTypes);
    }

    [Fact]
    public void ProcessorId_ShouldMatchConstructorArgument()
    {
        var (processor, _) = CreateProcessor();

        Assert.Equal("order-summary-v1", processor.ProcessorId);
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
