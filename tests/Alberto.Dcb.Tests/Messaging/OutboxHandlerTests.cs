using System.Collections.Concurrent;
using System.Text.Json;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Messaging;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Messaging;

/// <summary>
/// Tests that OutboxHandler correctly maps events to outbox entries.
/// </summary>
public class OutboxHandlerTests
{
    #region Test Events and Messages

    [EventType("order-placed")]
    public record OrderPlaced(Guid OrderId, decimal Amount) : IEvent;

    [EventType("order-shipped")]
    public record OrderShipped(Guid OrderId) : IEvent;

    [EventType("order-cancelled")]
    public record OrderCancelled(Guid OrderId) : IEvent;

    // Has fewer fields than OrderPlaced — the public contract shape
    [Message("order.placed", 1)]
    public record OrderPlacedMessage(Guid OrderId);

    [EventType("order-refunded")]
    public record OrderRefunded(Guid OrderId, string Reason, decimal Amount) : IEvent;

    // Projected contract with different fields
    [Message("order.refunded", 2)]
    public record OrderRefundedMessage(Guid OrderId, string Reason);

    public interface IOrderEnricher
    {
        string GetLabel(Guid orderId);
    }

    #endregion

    #region In-Memory Outbox Store

    private sealed class InMemoryOutboxStore : IOutboxStore
    {
        private readonly List<OutboxEntry> _entries = new();
        private readonly Dictionary<Guid, OutboxClaim> _claims = new();

        public IReadOnlyList<OutboxEntry> Entries => _entries;

        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
            int limit,
            TimeSpan claimLease,
            string claimedBy,
            CancellationToken ct = default)
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(claimLease);
            var pending = _entries
                .Where(e => e.Status == OutboxEntryStatus.Pending)
                .Take(limit)
                .ToList();
            var claims = new List<OutboxClaim>(pending.Count);
            foreach (var entry in pending)
            {
                var index = _entries.FindIndex(e => e.Id == entry.Id);
                var processing = entry with { Status = OutboxEntryStatus.Processing };
                _entries[index] = processing;
                var claim = new OutboxClaim(processing, Guid.NewGuid(), expiresAt);
                _claims[entry.Id] = claim;
                claims.Add(claim);
            }
            return Task.FromResult<IReadOnlyList<OutboxClaim>>(claims);
        }

        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default)
        {
            if (!_claims.TryGetValue(claim.Entry.Id, out var current)
                || current.Token != claim.Token
                || current.ExpiresAt <= DateTimeOffset.UtcNow)
                return Task.FromResult(false);

            var idx = _entries.FindIndex(e => e.Id == claim.Entry.Id);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with { Status = OutboxEntryStatus.Delivered, DeliveredAt = DateTimeOffset.UtcNow };
            _claims.Remove(claim.Entry.Id);
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default)
        {
            if (!_claims.TryGetValue(claim.Entry.Id, out var current)
                || current.Token != claim.Token
                || current.ExpiresAt <= DateTimeOffset.UtcNow)
                return Task.FromResult(false);

            var idx = _entries.FindIndex(e => e.Id == claim.Entry.Id);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with
                {
                    Status = OutboxEntryStatus.Failed,
                    RetryCount = _entries[idx].RetryCount + 1,
                    LastError = error
                };
            _claims.Remove(claim.Entry.Id);
            return Task.FromResult(true);
        }

        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Status == OutboxEntryStatus.Failed &&
                    (messageType is null || _entries[i].MessageType == messageType))
                {
                    _entries[i] = _entries[i] with { Status = OutboxEntryStatus.Pending, RetryCount = 0, LastError = null };
                    _claims.Remove(_entries[i].Id);
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
        Action<IMessageMappingRegistry> configure,
        IServiceProvider? sp = null)
    {
        var registry = new MessageMappingRegistry();
        configure(registry);
        var store = new InMemoryOutboxStore();
        var provider = sp ?? new ServiceCollection().BuildServiceProvider();
        var handler = new OutboxHandler(
            registry,
            store,
            provider.GetRequiredService<IServiceScopeFactory>());
        return (handler, store);
    }

    #endregion

    #region ProcessorId and HandledEventTypes

    [Fact]
    public void ProcessorId_IsOutbox()
    {
        var (handler, _) = CreateHandler(r =>
            r.Map<OrderPlaced>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(null)));
        Assert.Equal("outbox", handler.ProcessorId);
    }

    [Fact]
    public void HandledEventTypes_ReflectsMappedTypes()
    {
        var (handler, _) = CreateHandler(r =>
        {
            r.Map<OrderPlaced>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(null));
            r.Map<OrderShipped>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(null));
        });

        Assert.Contains("order-placed", handler.HandledEventTypes);
        Assert.Contains("order-shipped", handler.HandledEventTypes);
        Assert.DoesNotContain("order-cancelled", handler.HandledEventTypes);
    }

    [Fact]
    public void WithOutbox_DeclaresProcessorForStartupValidation()
    {
        var services = new ServiceCollection();
        services.AddAlberto("test", builder => builder
            .WithInMemory()
            .WithOutbox(
                mappings => mappings.Map<OrderPlaced>((_, _, _) =>
                    ValueTask.FromResult<ExternalMessage?>(null)),
                new InMemoryOutboxStore()));

        using var provider = services.BuildServiceProvider();
        var definition = provider
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("test");

        var declaration = Assert.Single(definition.Processors);
        Assert.Equal(OutboxHandler.ProcessorIdValue, declaration.ProcessorId);
        Assert.Equal(ProcessorKind.Reactor, declaration.Kind);
        Assert.Equal(ProcessorBatchingMode.Required, declaration.Execution.BatchingMode);
    }

    #endregion

    #region ProcessEventAsync (raw delegate)

    [Fact]
    public async Task ProcessEventAsync_MappedEvent_WritesEntryToStore()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>((env, _, _) =>
            {
                var evt = JsonSerializer.Deserialize<OrderPlaced>(env.EventData)!;
                return ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>()));
            }));

        var envelope = CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 99.99m));
        await handler.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Single(store.Entries);
        var entry = store.Entries[0];
        Assert.Equal(envelope.Id, entry.SourceEventId);
        Assert.Equal("order.placed", entry.MessageType);
        Assert.Equal("1", entry.Version);
        Assert.Equal(envelope.TenantId, entry.TenantId);
        Assert.Equal(OutboxEntryStatus.Pending, entry.Status);
        Assert.Equal(0, entry.RetryCount);
        Assert.Null(entry.LastError);
        Assert.Null(entry.DeliveredAt);
    }

    [Fact]
    public async Task ProcessEventAsync_MapperReturnsNull_DoesNotWriteEntry()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(null)));

        var envelope = CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m));
        await handler.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task ProcessEventAsync_UnmappedEventType_DoesNotWriteEntry()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>((env, _, _) =>
                ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>()))));

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
            r.Map<OrderPlaced>((_, _, _) =>
                ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", "{}", expectedMeta))));

        await handler.ProcessEventAsync(CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 1m)),
            TestContext.Current.CancellationToken);

        Assert.Single(store.Entries);
        Assert.Equal("abc123", store.Entries[0].Metadata["correlation-id"]);
    }

    [Fact]
    public async Task ProcessEventAsync_MapperScopeReceivesEnvelopeTenant()
    {
        string? mappedTenant = null;
        var services = new ServiceCollection();
        services.AddTenancy();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var (handler, store) = CreateHandler(
            r => r.Map<OrderPlaced>((env, sp, _) =>
            {
                mappedTenant = sp.GetRequiredService<ITenantAccessor>().TenantId;
                return ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage(
                        "order.placed",
                        "1",
                        env.EventData,
                        new Dictionary<string, string>()));
            }),
            provider);

        await handler.ProcessEventAsync(
            CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 1m)),
            TestContext.Current.CancellationToken);

        Assert.Equal("test", mappedTenant);
        Assert.Equal("test", Assert.Single(store.Entries).TenantId);
    }

    [Fact]
    public async Task ProcessEventAsync_MultipleEvents_WritesMultipleEntries()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>((env, _, _) =>
                ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>()))));

        await handler.ProcessEventAsync(CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m), 1),
            TestContext.Current.CancellationToken);
        await handler.ProcessEventAsync(CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 20m), 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Entries.Count);
    }

    #endregion

    #region Map<TEvent, TMessage> extension (attribute-derived type/version)

    [Fact]
    public void Map_WithMessageType_RegistersEventTypeFromEventAttribute()
    {
        var registry = new MessageMappingRegistry();
        registry.Map<OrderRefunded, OrderRefundedMessage>(evt => new OrderRefundedMessage(evt.OrderId, evt.Reason));
        Assert.Contains("order-refunded", registry.MappedEventTypes);
    }

    [Fact]
    public async Task Map_WithMessageType_UsesAttributeForTypeAndVersion()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderRefunded, OrderRefundedMessage>(evt => new OrderRefundedMessage(evt.OrderId, evt.Reason)));

        var orderId = Guid.NewGuid();
        await handler.ProcessEventAsync(
            CreateEnvelope(new OrderRefunded(orderId, "duplicate", 50m)),
            TestContext.Current.CancellationToken);

        Assert.Single(store.Entries);
        Assert.Equal("order.refunded", store.Entries[0].MessageType);
        Assert.Equal("2", store.Entries[0].Version);
    }

    [Fact]
    public async Task Map_WithMessageType_OnlyProjectsSelectedFields()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderRefunded, OrderRefundedMessage>(evt => new OrderRefundedMessage(evt.OrderId, evt.Reason)));

        await handler.ProcessEventAsync(
            CreateEnvelope(new OrderRefunded(Guid.NewGuid(), "duplicate", 99.99m)),
            TestContext.Current.CancellationToken);

        var payload = JsonDocument.Parse(store.Entries[0].Payload);
        Assert.False(payload.RootElement.TryGetProperty("Amount", out _), "Amount should not be in the external message");
        Assert.Equal("duplicate", payload.RootElement.GetProperty("Reason").GetString());
    }

    [Fact]
    public void Map_WithMessageType_ThrowsWhenAttributeMissing()
    {
        var registry = new MessageMappingRegistry();
        // OrderPlaced has [EventType] but no [Message]
        Assert.Throws<InvalidOperationException>(() =>
            registry.Map<OrderRefunded, OrderPlaced>(evt => new OrderPlaced(evt.OrderId, evt.Amount)));
    }

    #endregion

    #region ProcessBatchAsync (IBatchableProcessor)

    [Fact]
    public void OutboxHandler_ImplementsIBatchableProcessor()
    {
        var (handler, _) = CreateHandler(r =>
            r.Map<OrderPlaced>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(null)));
        Assert.IsAssignableFrom<IBatchableProcessor>(handler);
    }

    [Fact]
    public async Task ProcessBatchAsync_AllMapped_WritesAllEntries()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>((env, _, _) =>
                ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>()))));

        var envelopes = Enumerable.Range(1, 5)
            .Select(i => CreateEnvelope(new OrderPlaced(Guid.NewGuid(), i * 10m), i))
            .ToList();

        await handler.ProcessBatchAsync(envelopes, TestContext.Current.CancellationToken);

        Assert.Equal(5, store.Entries.Count);
        Assert.All(store.Entries, e => Assert.Equal("order.placed", e.MessageType));
    }

    [Fact]
    public async Task ProcessBatchAsync_SomeMappersReturnNull_OnlyInsertsNonNull()
    {
        var (handler, store) = CreateHandler(r =>
        {
            r.Map<OrderPlaced>((env, _, _) =>
                ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>())));
            // OrderShipped mapper suppresses
            r.Map<OrderShipped>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(null));
        });

        var envelopes = new List<IEventEnvelope>
        {
            CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m), 1),
            CreateEnvelope(new OrderShipped(Guid.NewGuid()), 2),
            CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 20m), 3),
        };

        await handler.ProcessBatchAsync(envelopes, TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Entries.Count);
        Assert.All(store.Entries, e => Assert.Equal("order.placed", e.MessageType));
    }

    [Fact]
    public async Task ProcessBatchAsync_SourceEventIdsPreserved()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>((env, _, _) =>
                ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>()))));

        var envelopes = Enumerable.Range(1, 3)
            .Select(i => CreateEnvelope(new OrderPlaced(Guid.NewGuid(), i), i))
            .ToList();

        await handler.ProcessBatchAsync(envelopes, TestContext.Current.CancellationToken);

        var expectedIds = envelopes.Select(e => e.Id).ToHashSet();
        Assert.All(store.Entries, e => Assert.Contains(e.SourceEventId, expectedIds));
    }

    [Fact]
    public async Task ProcessBatchAsync_EmptyBatch_WritesNothing()
    {
        var (handler, store) = CreateHandler(r =>
            r.Map<OrderPlaced>((env, _, _) =>
                ValueTask.FromResult<ExternalMessage?>(
                    new ExternalMessage("order.placed", "1", env.EventData, new Dictionary<string, string>()))));

        await handler.ProcessBatchAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(store.Entries);
    }

    #endregion

    #region Map<TEvent, TDep, TMessage> extension (with service injection)

    [Fact]
    public async Task Map_WithDependency_ResolvesServiceAndProjectsMessage()
    {
        var enricher = new FakeOrderEnricher("vip");
        var sp = new ServiceCollection()
            .AddSingleton<IOrderEnricher>(enricher)
            .BuildServiceProvider();

        var (handler, store) = CreateHandler(
            r => r.Map<OrderPlaced, IOrderEnricher, OrderPlacedMessage>(
                (enricher, evt) => new OrderPlacedMessage(evt.OrderId)),
            sp);

        await handler.ProcessEventAsync(
            CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m)),
            TestContext.Current.CancellationToken);

        Assert.Single(store.Entries);
        Assert.Equal("order.placed", store.Entries[0].MessageType);
        Assert.Equal("1", store.Entries[0].Version);
    }

    [Fact]
    public async Task Map_WithScopedDependency_CreatesAndDisposesOneScopePerEvent()
    {
        var tracker = new MappingScopeTracker();
        var resolvedIds = new ConcurrentBag<Guid>();
        var services = new ServiceCollection()
            .AddSingleton(tracker)
            .AddScoped<IOrderEnricher, ScopedOrderEnricher>();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var (handler, store) = CreateHandler(
            r => r.Map<OrderPlaced, IOrderEnricher, OrderPlacedMessage>(
                (enricher, evt) =>
                {
                    resolvedIds.Add(((ScopedOrderEnricher)enricher).InstanceId);
                    return new OrderPlacedMessage(evt.OrderId);
                }),
            provider);

        await handler.ProcessEventAsync(
            CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m), 1),
            TestContext.Current.CancellationToken);
        await handler.ProcessEventAsync(
            CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 20m), 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(2, resolvedIds.Distinct().Count());
        Assert.Equal(tracker.Created.Order(), tracker.Disposed.Order());
    }

    [Fact]
    public async Task ProcessBatchAsync_ScopedDependenciesAreNotSharedAcrossConcurrentMappings()
    {
        var tracker = new MappingScopeTracker();
        var resolvedIds = new ConcurrentBag<Guid>();
        var services = new ServiceCollection()
            .AddSingleton(tracker)
            .AddScoped<IOrderEnricher, ScopedOrderEnricher>();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var (handler, store) = CreateHandler(
            r => r.Map<OrderPlaced, IOrderEnricher, OrderPlacedMessage>(
                (enricher, evt) =>
                {
                    resolvedIds.Add(((ScopedOrderEnricher)enricher).InstanceId);
                    return new OrderPlacedMessage(evt.OrderId);
                }),
            provider);

        var events = Enumerable.Range(1, 8)
            .Select(i => CreateEnvelope(new OrderPlaced(Guid.NewGuid(), i), i))
            .ToList();

        await handler.ProcessBatchAsync(events, TestContext.Current.CancellationToken);

        Assert.Equal(events.Count, store.Entries.Count);
        Assert.Equal(events.Count, resolvedIds.Distinct().Count());
        Assert.Equal(tracker.Created.Order(), tracker.Disposed.Order());
    }

    [Fact]
    public async Task ProcessEventAsync_MapperFailureStillDisposesItsDependencyScope()
    {
        var tracker = new MappingScopeTracker();
        var services = new ServiceCollection()
            .AddSingleton(tracker)
            .AddScoped<IOrderEnricher, ScopedOrderEnricher>();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var (handler, _) = CreateHandler(
            r => r.Map<OrderPlaced>(async (_, sp, _) =>
            {
                _ = sp.GetRequiredService<IOrderEnricher>();
                await Task.Yield();
                throw new InvalidOperationException("mapping failed");
            }),
            provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.ProcessEventAsync(
                CreateEnvelope(new OrderPlaced(Guid.NewGuid(), 10m)),
                TestContext.Current.CancellationToken));

        Assert.Single(tracker.Created);
        Assert.Equal(tracker.Created, tracker.Disposed);
    }

    private sealed class FakeOrderEnricher(string label) : IOrderEnricher
    {
        public string GetLabel(Guid orderId) => label;
    }

    private sealed class ScopedOrderEnricher : IOrderEnricher, IAsyncDisposable
    {
        private readonly MappingScopeTracker _tracker;

        public ScopedOrderEnricher(MappingScopeTracker tracker)
        {
            _tracker = tracker;
            InstanceId = Guid.NewGuid();
            tracker.Created.Add(InstanceId);
        }

        public Guid InstanceId { get; }

        public string GetLabel(Guid orderId) => InstanceId.ToString();

        public ValueTask DisposeAsync()
        {
            _tracker.Disposed.Add(InstanceId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MappingScopeTracker
    {
        public ConcurrentBag<Guid> Created { get; } = [];
        public ConcurrentBag<Guid> Disposed { get; } = [];
    }

    #endregion
}
