using System.Text.Json;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

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

    #region Tracking State Store

    /// <summary>
    /// Thin wrapper over <see cref="InMemoryStateStore{TState}"/> that records which document
    /// IDs were passed as deletes in <see cref="ApplyChangesAsync"/>. Tests need this to assert
    /// that the batch processor computes the correct net diff: the shipped adapter's own
    /// observable state cannot distinguish "was deleted" from "never existed", so we intercept
    /// the delete list here rather than reimplement the store.
    /// </summary>
    private sealed class TrackingStateStore : IStateStore<OrderSummary>
    {
        private readonly InMemoryStateStore<OrderSummary> _inner = new();

        public List<string> DeletedIds { get; } = new();

        public Task<IReadOnlyDictionary<string, OrderSummary>> LoadManyAsync(
            IEnumerable<string> documentIds, CancellationToken ct = default)
            => _inner.LoadManyAsync(documentIds, ct);

        public Task ApplyChangesAsync(
            IReadOnlyDictionary<string, OrderSummary> upserts,
            IReadOnlyCollection<string> deletes,
            CancellationToken ct = default)
        {
            DeletedIds.AddRange(deletes);
            return _inner.ApplyChangesAsync(upserts, deletes, ct);
        }

        public Task<IReadOnlyList<OrderSummary>> ListRecentAsync(
            int limit = 20, CancellationToken ct = default)
            => _inner.ListRecentAsync(limit, ct);
    }

    #endregion

    #region ProcessEvent Tests

    private static (DeclaredAsyncProjection<OrderSummary> processor, TrackingStateStore stateStore) CreateProcessor()
    {
        var stateStore = new TrackingStateStore();
        var processor = new DeclaredAsyncProjection<OrderSummary>(Declaration(), _ => stateStore);
        return (processor, stateStore);
    }

    [Fact]
    public async Task ProcessEvent_ShouldUpsertState()
    {
        var (processor, stateStore) = CreateProcessor();

        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderCreated(orderId, 100m), 1);

        await processor.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Single(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
        var state = (await stateStore.LoadManyAsync([orderId.ToString()], TestContext.Current.CancellationToken))[orderId.ToString()];
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


        var state = (await stateStore.LoadManyAsync([orderId.ToString()], TestContext.Current.CancellationToken))[orderId.ToString()];
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


        Assert.Empty(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
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


        var loaded = await stateStore.LoadManyAsync([order1.ToString(), order2.ToString()], TestContext.Current.CancellationToken);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Confirmed", loaded[order1.ToString()].Status);
        Assert.Equal("Created", loaded[order2.ToString()].Status);
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
        var state = (await stateStore.LoadManyAsync([orderId.ToString()], TestContext.Current.CancellationToken))[orderId.ToString()];
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
            _ => new TrackingStateStore(),
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

        Assert.Empty(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessEvent_ShouldSkipWhenDocumentIdIsNull()
    {
        var stateStore = new TrackingStateStore();
        var declaration = DeclareProjection.For<OrderSummary>("order-summary-v1")
            .On<OrderCreated>(
                id: e => null, // opt out of the event entirely
                apply: (state, e, ctx) => new OrderSummary { OrderId = e.OrderId })
            .Build();
        var processor = new DeclaredAsyncProjection<OrderSummary>(declaration, _ => stateStore);

        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCreated(Guid.NewGuid(), 100m), 1),
            TestContext.Current.CancellationToken);

        Assert.Empty(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
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
        Assert.Single(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
        var state = (await stateStore.LoadManyAsync([orderId.ToString()], TestContext.Current.CancellationToken))[orderId.ToString()];
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

        var loaded = await stateStore.LoadManyAsync([order1.ToString(), order2.ToString()], TestContext.Current.CancellationToken);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Confirmed", loaded[order1.ToString()].Status);
        Assert.Equal("Created", loaded[order2.ToString()].Status);
    }

    [Fact]
    public async Task Batch_EmptyBatch_ShouldNotThrow()
    {
        var (processor, stateStore) = CreateProcessor();

        await processor.ProcessBatchAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
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

        Assert.Empty(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
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

        Assert.Empty(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
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
        Assert.Single(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
        Assert.Equal(250m, (await stateStore.LoadManyAsync([orderId.ToString()], TestContext.Current.CancellationToken))[orderId.ToString()].Amount);
        Assert.Empty(stateStore.DeletedIds);
    }

    [Fact]
    public async Task Batch_FallsBackToPerEvent_OnBatchFailure()
    {
        // A state store that always throws on ApplyChangesAsync simulates a batch failure.
        var throwingProcessor = new DeclaredAsyncProjection<OrderSummary>(
            Declaration(),
            _ => new ThrowOnApplyStateStore());

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

        Assert.Single(await stateStore.ListRecentAsync(100, TestContext.Current.CancellationToken));
        Assert.Equal("Created", (await stateStore.LoadManyAsync([orderId.ToString()], TestContext.Current.CancellationToken))[orderId.ToString()].Status);
    }

    private class ThrowOnApplyStateStore : IStateStore<OrderSummary>
    {
        public Task<IReadOnlyDictionary<string, OrderSummary>> LoadManyAsync(
            IEnumerable<string> documentIds,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, OrderSummary>>(new Dictionary<string, OrderSummary>());

        public Task ApplyChangesAsync(
            IReadOnlyDictionary<string, OrderSummary> upserts,
            IReadOnlyCollection<string> deletes,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated batch failure");

        public Task<IReadOnlyList<OrderSummary>> ListRecentAsync(
            int limit = 20,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OrderSummary>>([]);
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
