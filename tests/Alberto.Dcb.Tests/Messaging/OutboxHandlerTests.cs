using System.Text.Json;
using Alberto.Dcb.Messaging;
using Xunit;

namespace Alberto.Dcb.Tests.Messaging;

/// <summary>
/// Tests that OutboxHandler correctly maps events to outbox entries.
/// </summary>
public class OutboxHandlerTests
{
    #region Test Events

    [EventType("order-placed")]
    public record OrderPlaced(Guid OrderId, decimal Amount) : IEvent;

    [EventType("order-shipped")]
    public record OrderShipped(Guid OrderId) : IEvent;

    [EventType("order-cancelled")]
    public record OrderCancelled(Guid OrderId) : IEvent;

    #endregion

    #region In-Memory Outbox Store

    private sealed class InMemoryOutboxStore : IOutboxStore
    {
        private readonly List<OutboxEntry> _entries = new();

        public IReadOnlyList<OutboxEntry> Entries => _entries;

        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit = 100, CancellationToken ct = default)
        {
            IReadOnlyList<OutboxEntry> result = _entries
                .Where(e => e.Status == OutboxEntryStatus.Pending)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }

        public Task MarkDeliveredAsync(Guid id, CancellationToken ct = default)
        {
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with { Status = OutboxEntryStatus.Delivered, DeliveredAt = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default)
        {
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with
                {
                    Status = OutboxEntryStatus.Failed,
                    RetryCount = _entries[idx].RetryCount + 1,
                    LastError = error
                };
            return Task.CompletedTask;
        }

        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Status == OutboxEntryStatus.Failed &&
                    (messageType is null || _entries[i].MessageType == messageType))
                {
                    _entries[i] = _entries[i] with { Status = OutboxEntryStatus.Pending, RetryCount = 0, LastError = null };
                }
            }
            return Task.CompletedTask;
        }

        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e.Status == OutboxEntryStatus.Delivered && e.DeliveredAt < before);
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Helpers

    private static IEventEnvelope CreateEnvelope<TEvent>(TEvent @event, long position = 1) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "test",
            GlobalPosition = position,
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };
    }

    private static (OutboxHandler handler, InMemoryOutboxStore store) CreateHandler(
        Action<IMessageMappingRegistry> configure)
    {
        var registry = new MessageMappingRegistry();
        configure(registry);
        var store = new InMemoryOutboxStore();
        var handler = new OutboxHandler(registry, store);
        return (handler, store);
    }

    #endregion

    #region ProcessorId and HandledEventTypes

    [Fact]
    public void ProcessorId_IsOutbox()
    {
        var (handler, _) = CreateHandler(r => r.Map<OrderPlaced>(_ => null));
        Assert.Equal("outbox", handler.ProcessorId);
    }

    [Fact]
    public void HandledEventTypes_ReflectsMappedTypes()
    {
        var (handler, _) = CreateHandler(r =>
        {
            r.Map<OrderPlaced>(_ => null);
            r.Map<OrderShipped>(_ => null);
        });

        Assert.Contains("order-placed", handler.HandledEventTypes);
        Assert.Contains("order-shipped", handler.HandledEventTypes);
        Assert.DoesNotContain("order-cancelled", handler.HandledEventTypes);
    }

    #endregion

    #region ProcessEventAsync

    [Fact]
    public async Task ProcessEventAsync_MappedEvent_WritesEntryToStore()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>(env =>
            {
                var evt = JsonSerializer.Deserialize<OrderPlaced>(env.EventData)!;
                return new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>());
            }));

        var envelope = CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 99.99m));
        await handler.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Single(store.Entries);
        var entry = store.Entries[0];
        Assert.Equal(envelope.Id, entry.SourceEventId);
        Assert.Equal("order.placed", entry.MessageType);
        Assert.Equal("1", entry.Version);
        Assert.Equal(OutboxEntryStatus.Pending, entry.Status);
        Assert.Equal(0, entry.RetryCount);
        Assert.Null(entry.LastError);
        Assert.Null(entry.DeliveredAt);
    }

    [Fact]
    public async Task ProcessEventAsync_MapperReturnsNull_DoesNotWriteEntry()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>(_ => null));

        var envelope = CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m));
        await handler.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task ProcessEventAsync_UnmappedEventType_DoesNotWriteEntry()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>(env =>
                new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>())));

        // OrderCancelled is not mapped
        var envelope = CreateEnvelope(new OrderCancelled(Guid.NewGuid()));
        await handler.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task ProcessEventAsync_PreservesMetadataFromMessage()
    {
        var expectedMeta = new Dictionary<string, string> { ["correlation-id"] = "abc123" };
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>(_ =>
                new ExternalMessage("order.placed", "1", "{}", expectedMeta)));

        await handler.ProcessEventAsync(CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 1m)),
            TestContext.Current.CancellationToken);

        Assert.Single(store.Entries);
        Assert.Equal("abc123", store.Entries[0].Metadata["correlation-id"]);
    }

    [Fact]
    public async Task ProcessEventAsync_MultipleEvents_WritesMultipleEntries()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>(env =>
                new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>())));

        await handler.ProcessEventAsync(CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m), 1),
            TestContext.Current.CancellationToken);
        await handler.ProcessEventAsync(CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 20m), 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Entries.Count);
    }

    #endregion
}
