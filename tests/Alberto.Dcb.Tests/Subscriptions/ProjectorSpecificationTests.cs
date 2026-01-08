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
    }

    #endregion

    #region ProcessBatch Tests

    private static (IEventProcessor processor, InMemoryStateStore stateStore, InMemoryCheckpointStore checkpointStore) CreateProcessor()
    {
        var stateStore = new InMemoryStateStore();
        var checkpointStore = new InMemoryCheckpointStore();
        var processor = new AsyncProjection<OrderSummary, OrderSummaryProjection>(
            stateStore, checkpointStore, "order-summary-v1");
        return (processor, stateStore, checkpointStore);
    }

    [Fact]
    public async Task ProcessBatch_ShouldUpsertState()
    {
        var (processor, stateStore, _) = CreateProcessor();

        var orderId = Guid.NewGuid();
        var events = new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        };

        await processor.ProcessBatchAsync(events, TestContext.Current.CancellationToken);

        Assert.Single(stateStore.Store);
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal(orderId, state.OrderId);
        Assert.Equal(100m, state.Amount);
        Assert.Equal("Created", state.Status);
    }

    [Fact]
    public async Task ProcessBatch_ShouldUpdateExistingState()
    {
        var (processor, stateStore, _) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // First batch: create
        await processor.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        }, TestContext.Current.CancellationToken);

        // Second batch: confirm
        await processor.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderConfirmed(orderId), 2)
        }, TestContext.Current.CancellationToken);

        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
        Assert.Equal(100m, state.Amount); // Preserved
    }

    [Fact]
    public async Task ProcessBatch_ShouldDeleteOnCancellation()
    {
        var (processor, stateStore, _) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // Create
        await processor.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        }, TestContext.Current.CancellationToken);

        // Cancel
        await processor.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCancelled(orderId), 2)
        }, TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
        Assert.Contains(orderId.ToString(), stateStore.DeletedIds);
    }

    [Fact]
    public async Task ProcessBatch_ShouldHandleMultipleDocuments()
    {
        var (processor, stateStore, _) = CreateProcessor();

        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();

        await processor.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(order1, 100m), 1),
            CreateEnvelope(new OrderCreated(order2, 200m), 2),
            CreateEnvelope(new OrderConfirmed(order1), 3)
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, stateStore.Store.Count);
        Assert.Equal("Confirmed", stateStore.Store[order1.ToString()].Status);
        Assert.Equal("Created", stateStore.Store[order2.ToString()].Status);
    }

    [Fact]
    public async Task ProcessBatch_ShouldFoldEventsForSameDocument()
    {
        var (processor, stateStore, _) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // All events for same order in one batch
        await processor.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1),
            CreateEnvelope(new OrderConfirmed(orderId), 2)
        }, TestContext.Current.CancellationToken);

        // Should have folded to final state
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
    }

    [Fact]
    public async Task ProcessBatch_ShouldSaveCheckpoint()
    {
        var (processor, _, checkpointStore) = CreateProcessor();

        await processor.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(Guid.NewGuid(), 100m), 10),
            CreateEnvelope(new OrderCreated(Guid.NewGuid(), 200m), 15)
        }, TestContext.Current.CancellationToken);

        var checkpoint = await checkpointStore.GetAsync(processor.ProcessorId, TestContext.Current.CancellationToken);
        Assert.Equal(15, checkpoint);
    }

    [Fact]
    public async Task ProcessBatch_EmptyBatch_ShouldNotThrow()
    {
        var (processor, _, _) = CreateProcessor();

        var result = await processor.ProcessBatchAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingResult.Continue, result);
    }

    [Fact]
    public void HandledEventTypes_ShouldContainProjectionTypes()
    {
        var (processor, _, _) = CreateProcessor();

        Assert.Contains("order-created", processor.HandledEventTypes);
        Assert.Contains("order-confirmed", processor.HandledEventTypes);
        Assert.Contains("order-cancelled", processor.HandledEventTypes);
    }

    [Fact]
    public void ProcessorId_ShouldMatchConstructorArgument()
    {
        var (processor, _, _) = CreateProcessor();

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
