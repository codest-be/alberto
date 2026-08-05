using System.Collections.Concurrent;
using System.Text.Json;
using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Alberto.Testing;
using Alberto.Upcasting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Messaging;

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

    // V1 of the module-key serializer test event: no [EventType], lives only in the upcast chain.
    public record ModuleKeyTestEventV1(string Name);

    // V2: current version — adds Extra, which the upcaster fills with "upcasted".
    [EventType("omk-order", Version = 2)]
    public record ModuleKeyTestEventV2(string Name, string Extra) : IEvent;

    [Message("omk-order-msg", 1)]
    public record ModuleKeyTestMessage(string Extra);

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
        IServiceProvider? sp = null,
        string? moduleKey = null)
    {
        var registry = new MessageMappingRegistry(moduleKey);
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
        var registry = new MessageMappingRegistry(null);
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
        var registry = new MessageMappingRegistry(null);
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

    #region ModuleKey — keyed serializer resolved at map time

    // Builds a serializer that knows the V1→V2 upcaster for "omk-order".
    // The upcaster fills Extra with the given sentinel so tests can verify which serializer fired.
    private static EventSerializer BuildShardSerializer(string sentinel) =>
        EventSerializer
            .FromRegistry(new Dictionary<string, Type>
            {
                ["omk-order"] = typeof(ModuleKeyTestEventV2)
            })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<ModuleKeyTestEventV2>("omk-order")
                    .From<ModuleKeyTestEventV1>(1, v1 => new ModuleKeyTestEventV2(v1.Name, sentinel))
                    .Build())
                .Build());

    // Convenience wrapper used by the single-module test.
    private static EventSerializer BuildModuleKeyTestSerializer() => BuildShardSerializer("upcasted");

    private static IEventEnvelope MakeOmkV1Envelope() => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType("omk-order", 1),
        EventData = JsonSerializer.Serialize(new ModuleKeyTestEventV1("hello")),
        Tags = [],
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
        TenantId = "test",
    };

    /// <summary>
    /// Positive test: the convenience Map&lt;TEvent, TMessage&gt; extension resolves the
    /// module-keyed <see cref="EventSerializer"/> so the upcaster chain fires on a v1 envelope.
    /// Asserts that a registry constructed with a module key correctly routes to that key's
    /// serializer and the upcasted event value is delivered to the mapper.
    /// </summary>
    [Fact]
    public async Task Map_WithMessageType_ResolvesModuleKeyedSerializerSoUpcasterChainFires()
    {
        const string moduleKey = "omk-module";

        var services = new ServiceCollection();
        services.AddKeyedSingleton<EventSerializer>(moduleKey, BuildModuleKeyTestSerializer());
        // No null-keyed serializer registered — any GetKeyedService(null) call returns null.
        await using var provider = services.BuildServiceProvider();

        var registry = new MessageMappingRegistry(moduleKey);

        string? capturedExtra = null;
        registry.Map<ModuleKeyTestEventV2, ModuleKeyTestMessage>(evt =>
        {
            capturedExtra = evt.Extra;
            return new ModuleKeyTestMessage(evt.Extra);
        });

        await registry.TryMapAsync(MakeOmkV1Envelope(), provider, default);

        // If the keyed serializer was bypassed, this would never be reached (EV-1 guard throws).
        Assert.Equal("upcasted", capturedExtra);
    }

    /// <summary>
    /// Regression test: in a sharded module, each shard's <c>WithOutbox</c> registry
    /// must carry that shard's own service key so it resolves the correct shard-keyed
    /// <see cref="EventSerializer"/> — not the last shard's.
    ///
    /// Before the fix: <c>WithOutbox</c> constructed one <see cref="MessageMappingRegistry"/>
    /// eagerly (outside any <c>Register</c> callback) and each shard's callback mutated
    /// <c>registry.ModuleKey</c> via the now-removed setter. With two shards the last shard
    /// won: <c>registry.ModuleKey</c> was overwritten, so every mapper in every shard resolved
    /// <c>GetKeyedService&lt;EventSerializer&gt;(lastShard)</c> — the first shard's upcaster
    /// chain was silently bypassed.
    ///
    /// After the fix: <c>WithOutbox</c>'s <c>Register</c> callback constructs a fresh
    /// <see cref="MessageMappingRegistry"/> per shard, each carrying an immutable key for
    /// exactly that shard.
    ///
    /// The deferred registrations are run manually here so the test stays in the fast
    /// (non-integration) suite without Postgres or full <c>AddAlberto</c> module plumbing.
    /// </summary>
    [Fact]
    public async Task WithOutbox_in_sharded_module_each_shard_resolves_its_own_keyed_serializer()
    {
        const string logicalKey = "orders";
        const string shardDb1Key = "orders#db1";
        const string shardDb2Key = "orders#db2";

        var outboxStore = new InMemoryOutboxStore();
        var builder = new DcbModuleBuilder(logicalKey);

        // Capture the registry that configureMappings receives for each shard.
        // After the fix: called once per shard (inside the Register callback) → two entries.
        // Before the fix: called once eagerly (outside the callback) → one entry, shared by both.
        var registries = new List<IMessageMappingRegistry>();
        var capturedExtras = new List<string?>();

        builder.WithOutbox(
            registry =>
            {
                var index = registries.Count;
                registries.Add(registry);
                capturedExtras.Add(null);
                registry.Map<ModuleKeyTestEventV2, ModuleKeyTestMessage>(evt =>
                {
                    capturedExtras[index] = evt.Extra;
                    return new ModuleKeyTestMessage(evt.Extra);
                });
            },
            outboxStore);

        var services = new ServiceCollection();
        // Each shard has its own serializer: distinguishable by the sentinel in Extra.
        services.AddKeyedSingleton<EventSerializer>(shardDb1Key, BuildShardSerializer("shard-a"));
        services.AddKeyedSingleton<EventSerializer>(shardDb2Key, BuildShardSerializer("shard-b"));
        // No null-keyed serializer — any GetKeyedService<EventSerializer>(null) returns null.

        // Simulate what RegisterModule does for two shards: run the deferred registrations
        // once per shard, each time with the shard's own AlbertoModuleContext.
        var db1Def = new AlbertoModuleDefinition { ModuleKey = logicalKey, ShardId = "db1" };
        foreach (var reg in builder.DeferredRegistrations)
            reg(new AlbertoModuleContext(services, db1Def));

        var db2Def = new AlbertoModuleDefinition { ModuleKey = logicalKey, ShardId = "db2" };
        foreach (var reg in builder.DeferredRegistrations)
            reg(new AlbertoModuleContext(services, db2Def));

        await using var provider = services.BuildServiceProvider();

        // After the fix: configureMappings runs once per shard → two separate registries,
        // each with the correct immutable key.
        // Before the fix: configureMappings ran once eagerly → one shared registry,
        // ModuleKey = "orders#db2" (last shard wins) → Assert.Equal(2, 1) fails here.
        Assert.Equal(2, registries.Count);
        Assert.Equal(shardDb1Key, registries[0].ModuleKey);
        Assert.Equal(shardDb2Key, registries[1].ModuleKey);

        // Map a v1 envelope through each registry. Each must resolve its own serializer so
        // the shard-specific upcaster fills Extra with the correct sentinel.
        // Before the fix: registries[0].ModuleKey would have been "orders#db2", so both
        // shards would resolve the shard-b serializer and capturedExtras[0] would be "shard-b".
        await registries[0].TryMapAsync(MakeOmkV1Envelope(), provider, default);
        await registries[1].TryMapAsync(MakeOmkV1Envelope(), provider, default);

        Assert.Equal("shard-a", capturedExtras[0]);
        Assert.Equal("shard-b", capturedExtras[1]);
    }

    #endregion
}
