using System.Text.Json;
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

    private static ProjectionDeclaration<OrderSummary> Declaration() =>
        DeclareProjection.For<OrderSummary>("order-summary-v1")
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
            .On<OrderCancelled>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => ProjectionResults.Delete<OrderSummary>())
            .Build();

    #endregion

    #region In-Memory State Store

    private class InMemoryStateStore : IStateStore<OrderSummary>
    {
        private readonly Dictionary<string, OrderSummary> _store = new();

        public IReadOnlyDictionary<string, OrderSummary> Store => _store;
        public List<string> DeletedIds { get; } = new();

        public Task<Dictionary<string, OrderSummary>> LoadManyAsync(
            IEnumerable<string> documentIds,
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

    #region ProcessEvent Tests

    private static (DeclaredAsyncProjection<OrderSummary> processor, InMemoryStateStore stateStore) CreateProcessor()
    {
        var stateStore = new InMemoryStateStore();
        var processor = new DeclaredAsyncProjection<OrderSummary>(Declaration(), () => stateStore);
        return (processor, stateStore);
    }

    [Fact]
    public async Task ProcessEvent_ShouldUpsertState()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderCreated(orderId, 100m), 1);

        await processor.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);
        await processor.FlushAsync(TestContext.Current.CancellationToken);

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

        await processor.FlushAsync(TestContext.Current.CancellationToken);

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

        await processor.FlushAsync(TestContext.Current.CancellationToken);

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

        await processor.FlushAsync(TestContext.Current.CancellationToken);

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

        await processor.FlushAsync(TestContext.Current.CancellationToken);

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
    public void ProcessorId_ShouldComeFromDeclaration()
    {
        var (processor, _) = CreateProcessor();

        Assert.Equal("order-summary-v1", processor.ProcessorId);
    }

    [Fact]
    public void ProcessorId_ShouldHonourOverride()
    {
        var processor = new DeclaredAsyncProjection<OrderSummary>(
            Declaration(),
            () => new InMemoryStateStore(),
            processorIdOverride: "order-summary-shadow");

        Assert.Equal("order-summary-shadow", processor.ProcessorId);
    }

    [Fact]
    public async Task ProcessEvent_ShouldIgnoreUnhandledEventTypes()
    {
        var (processor, stateStore) = CreateProcessor();

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            GlobalPosition = 1,
            EventType = new EventType("some-unrelated-event"),
            Tags = [],
            EventData = "{}",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };

        await processor.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
    }

    [Fact]
    public async Task ProcessEvent_ShouldSkipWhenDocumentIdIsNull()
    {
        var stateStore = new InMemoryStateStore();
        var declaration = DeclareProjection.For<OrderSummary>("order-summary-v1")
            .On<OrderCreated>(
                id: e => null, // opt out of the event entirely
                apply: (state, e, ctx) => new OrderSummary { OrderId = e.OrderId })
            .Build();
        var processor = new DeclaredAsyncProjection<OrderSummary>(declaration, () => stateStore);

        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCreated(Guid.NewGuid(), 100m), 1),
            TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
    }

    #endregion

    #region ProcessBatch Tests

    [Fact]
    public async Task Batch_MultipleEventsForSameDocument_AppliesInOrder()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // Three events for the same document in one batch
        var batch = new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(orderId, 100m), 1),
            CreateEnvelope(new OrderConfirmed(orderId), 2),
        };

        await processor.ProcessBatchAsync(batch, TestContext.Current.CancellationToken);

        // State should reflect events applied in order: Created → Confirmed
        Assert.Single(stateStore.Store);
        var state = stateStore.Store[orderId.ToString()];
        Assert.Equal("Confirmed", state.Status);
        Assert.Equal(100m, state.Amount); // Preserved from OrderCreated
    }

    [Fact]
    public async Task Batch_MultipleDocumentsInOneBatch_AllPersisted()
    {
        var (processor, stateStore) = CreateProcessor();

        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();

        var batch = new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(order1, 50m), 1),
            CreateEnvelope(new OrderCreated(order2, 75m), 2),
            CreateEnvelope(new OrderConfirmed(order1), 3),
        };

        await processor.ProcessBatchAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal(2, stateStore.Store.Count);
        Assert.Equal("Confirmed", stateStore.Store[order1.ToString()].Status);
        Assert.Equal("Created", stateStore.Store[order2.ToString()].Status);
    }

    [Fact]
    public async Task Batch_EmptyBatch_ShouldNotThrow()
    {
        var (processor, stateStore) = CreateProcessor();

        await processor.ProcessBatchAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
    }

    [Fact]
    public async Task Batch_UnhandledEventTypes_ShouldBeIgnored()
    {
        var (processor, stateStore) = CreateProcessor();

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

        await processor.ProcessBatchAsync([envelope], TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
    }

    [Fact]
    public async Task Batch_DeleteInBatch_DocumentRemoved()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();

        // Pre-populate via a prior batch
        await processor.ProcessBatchAsync(
            [CreateEnvelope(new OrderCreated(orderId, 100m), 1)],
            TestContext.Current.CancellationToken);

        // Now cancel in the next batch
        await processor.ProcessBatchAsync(
            [CreateEnvelope(new OrderCancelled(orderId), 2)],
            TestContext.Current.CancellationToken);

        Assert.Empty(stateStore.Store);
        Assert.Contains(orderId.ToString(), stateStore.DeletedIds);
    }

    [Fact]
    public async Task Batch_DeleteThenRecreateInSameBatch_EndsUpUpserted()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();

        await processor.ProcessBatchAsync(
            [
                CreateEnvelope(new OrderCreated(orderId, 100m), 1),
                CreateEnvelope(new OrderCancelled(orderId), 2),
                CreateEnvelope(new OrderCreated(orderId, 250m), 3),
            ],
            TestContext.Current.CancellationToken);

        // The trailing Set wins over the earlier Delete
        Assert.Single(stateStore.Store);
        Assert.Equal(250m, stateStore.Store[orderId.ToString()].Amount);
        Assert.Empty(stateStore.DeletedIds);
    }

    [Fact]
    public async Task Batch_FallsBackToPerEvent_OnBatchFailure()
    {
        // A state store that always throws on ApplyChangesAsync simulates a batch failure.
        var throwingProcessor = new DeclaredAsyncProjection<OrderSummary>(
            Declaration(),
            () => new ThrowOnApplyStateStore());

        var orderId = Guid.NewGuid();
        var batch = new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderCreated(orderId, 200m), 1),
        };

        // Batch processing throws — the consumer's fallback is to retry per-event.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => throwingProcessor.ProcessBatchAsync(batch, TestContext.Current.CancellationToken));

        // The per-event path on a healthy store still works.
        var (processor, stateStore) = CreateProcessor();
        await processor.ProcessEventAsync(batch[0], TestContext.Current.CancellationToken);
        await processor.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Single(stateStore.Store);
        Assert.Equal("Created", stateStore.Store[orderId.ToString()].Status);
    }

    private class ThrowOnApplyStateStore : IStateStore<OrderSummary>
    {
        public Task<Dictionary<string, OrderSummary>> LoadManyAsync(
            IEnumerable<string> documentIds,
            CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, OrderSummary>());

        public Task ApplyChangesAsync(
            IReadOnlyDictionary<string, OrderSummary> upserts,
            IReadOnlyCollection<string> deletes,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated batch failure");
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
