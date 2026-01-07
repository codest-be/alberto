using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for the Projector<TState, TProjection> base class.
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

    #region Test Projector with In-Memory Store

    public class InMemoryOrderSummaryProjector : Projector<OrderSummary, OrderSummaryProjection>
    {
        private readonly Dictionary<string, OrderSummary> _store = new();
        private readonly List<string> _deletedIds = new();

        public InMemoryOrderSummaryProjector(ICheckpointStore checkpointStore)
            : base(checkpointStore)
        {
        }

        public override string ProcessorId => "order-summary-v1";

        public IReadOnlyDictionary<string, OrderSummary> Store => _store;
        public IReadOnlyList<string> DeletedIds => _deletedIds;

        protected override Task<Dictionary<string, OrderSummary>> LoadManyAsync(
            IEnumerable<string> documentIds, CancellationToken ct)
        {
            var result = new Dictionary<string, OrderSummary>();
            foreach (var id in documentIds)
            {
                if (_store.TryGetValue(id, out var state))
                    result[id] = state;
            }
            return Task.FromResult(result);
        }

        protected override Task ApplyChangesAsync(
            IReadOnlyDictionary<string, OrderSummary> upserts,
            IReadOnlyCollection<string> deletes,
            CancellationToken ct)
        {
            foreach (var (id, state) in upserts)
            {
                _store[id] = state;
            }

            foreach (var id in deletes)
            {
                _store.Remove(id);
                _deletedIds.Add(id);
            }

            return Task.CompletedTask;
        }
    }

    #endregion

    #region ProcessBatch Tests

    [Fact]
    public async Task ProcessBatch_ShouldUpsertState()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        var orderId = Guid.NewGuid();
        var events = new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        };

        await projector.ProcessBatchAsync(events);

        Assert.Single(projector.Store);
        var state = projector.Store[orderId.ToString()];
        Assert.Equal(orderId, state.OrderId);
        Assert.Equal(100m, state.Amount);
        Assert.Equal("Created", state.Status);
    }

    [Fact]
    public async Task ProcessBatch_ShouldUpdateExistingState()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        var orderId = Guid.NewGuid();

        // First batch: create
        await projector.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        });

        // Second batch: confirm
        await projector.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderConfirmed(orderId), 2)
        });

        var state = projector.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
        Assert.Equal(100m, state.Amount); // Preserved
    }

    [Fact]
    public async Task ProcessBatch_ShouldDeleteOnCancellation()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        var orderId = Guid.NewGuid();

        // Create
        await projector.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1)
        });

        // Cancel
        await projector.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCancelled(orderId), 2)
        });

        Assert.Empty(projector.Store);
        Assert.Contains(orderId.ToString(), projector.DeletedIds);
    }

    [Fact]
    public async Task ProcessBatch_ShouldHandleMultipleDocuments()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();

        await projector.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(order1, 100m), 1),
            CreateEnvelope(new OrderCreated(order2, 200m), 2),
            CreateEnvelope(new OrderConfirmed(order1), 3)
        });

        Assert.Equal(2, projector.Store.Count);
        Assert.Equal("Confirmed", projector.Store[order1.ToString()].Status);
        Assert.Equal("Created", projector.Store[order2.ToString()].Status);
    }

    [Fact]
    public async Task ProcessBatch_ShouldFoldEventsForSameDocument()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        var orderId = Guid.NewGuid();

        // All events for same order in one batch
        await projector.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1),
            CreateEnvelope(new OrderConfirmed(orderId), 2)
        });

        // Should have folded to final state
        var state = projector.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
    }

    [Fact]
    public async Task ProcessBatch_ShouldSaveCheckpoint()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        await projector.ProcessBatchAsync(new[]
        {
            CreateEnvelope(new OrderCreated(Guid.NewGuid(), 100m), 10),
            CreateEnvelope(new OrderCreated(Guid.NewGuid(), 200m), 15)
        });

        var checkpoint = await checkpointStore.GetAsync(projector.ProcessorId);
        Assert.Equal(15, checkpoint);
    }

    [Fact]
    public async Task ProcessBatch_EmptyBatch_ShouldNotThrow()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        var result = await projector.ProcessBatchAsync([]);

        Assert.Equal(ProcessingResult.Continue, result);
    }

    [Fact]
    public void Projection_ShouldBeAccessibleForTesting()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var projector = new InMemoryOrderSummaryProjector(checkpointStore);

        // Can access pure projection for unit testing
        var projection = projector.Projection;

        Assert.NotNull(projection);
        Assert.Contains("order-created", projection.HandledEventTypes);
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
