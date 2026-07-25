using System.Reflection;
using System.Text.Json;
using Alberto.Dcb.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Dcb.Tests;

public sealed class CommandPipelineTests
{
    [EventType("pipeline-order-created")]
    private sealed record OrderCreated : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    [EventType("pipeline-order-reserved")]
    private sealed record OrderReserved : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    [EventType("pipeline-order-confirmed")]
    private sealed record OrderConfirmed : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    private sealed record ConfirmOrder(Guid OrderId);

    [EventType("pipeline-scope-probe")]
    private sealed record ScopeProbe : IEvent;

    private sealed record OrderState
    {
        public static readonly OrderState Initial = new();
        public int EventCount { get; init; }
    }

    private sealed record LoadedOrder(OrderState State, DcbQuery Query, long LastPosition);

    [Fact]
    public async Task Persist_WithFoldLoad_ShouldUseExpectedPosition()
    {
        var backend = new InMemoryEventStoreBackend();
        var eventStore = new EventStore(backend);
        var serializer = CreateSerializer();
        var store = new AlbertoStore(eventStore, serializer);
        var orderId = Guid.NewGuid();
        var orderTag = new EventTag("order", orderId.ToString());

        await eventStore.AppendAsync([ToPersist(serializer, new OrderCreated { OrderId = orderId })], cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            store.Handle(new ConfirmOrder(orderId))
                .NoValidation()
                .Load(DcbQuery.ByTags(orderTag), OrderState.Initial, Apply)
                .Decide(async (_, _, ct) =>
                {
                    await eventStore.AppendAsync([ToPersist(serializer, new OrderReserved { OrderId = orderId })], cancellationToken: ct);
                    return Decision.Succeed(new OrderConfirmed { OrderId = orderId });
                })
                .Persist(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Persist_WithCustomBoundaryLoad_ShouldUseExpectedPosition()
    {
        var backend = new InMemoryEventStoreBackend();
        var eventStore = new EventStore(backend);
        var serializer = CreateSerializer();
        var store = new AlbertoStore(eventStore, serializer);
        var orderId = Guid.NewGuid();
        var orderTag = new EventTag("order", orderId.ToString());

        await eventStore.AppendAsync([ToPersist(serializer, new OrderCreated { OrderId = orderId })], cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            store.Handle(new ConfirmOrder(orderId))
                .NoValidation()
                .Load(
                    async _ =>
                    {
                        var (state, lastPosition) = await store.FoldWithPosition(
                            DcbQuery.ByTags(orderTag),
                            OrderState.Initial,
                            Apply,
                            TestContext.Current.CancellationToken);
                        return new LoadedOrder(state, DcbQuery.ByTags(orderTag), lastPosition);
                    },
                    loaded => loaded.Query,
                    loaded => loaded.LastPosition)
                .Decide(async (_, _, ct) =>
                {
                    await eventStore.AppendAsync([ToPersist(serializer, new OrderReserved { OrderId = orderId })], cancellationToken: ct);
                    return Decision.Succeed(new OrderConfirmed { OrderId = orderId });
                })
                .Persist(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Persist_WithExplicitBoundary_ShouldRejectAnEventAppendedAfterObservation()
    {
        var backend = new InMemoryEventStoreBackend();
        var eventStore = new EventStore(backend);
        var serializer = CreateSerializer();
        var store = new AlbertoStore(eventStore, serializer);
        var orderId = Guid.NewGuid();
        var query = DcbQuery.ByTags(new EventTag("order", orderId.ToString()));

        var decision = store.Handle(new ConfirmOrder(orderId))
            .NoValidation()
            .Decide(command => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }));

        await eventStore.AppendAsync(
            [ToPersist(serializer, new OrderReserved { OrderId = orderId })],
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DcbConflictException>(
            () => decision.Persist(query, expectedPosition: 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenericPersist_WithExplicitBoundary_ShouldRejectAnEventAppendedAfterObservation()
    {
        var backend = new InMemoryEventStoreBackend();
        var eventStore = new EventStore(backend);
        var serializer = CreateSerializer();
        var store = new AlbertoStore(eventStore, serializer);
        var orderId = Guid.NewGuid();
        var query = DcbQuery.ByTags(new EventTag("order", orderId.ToString()));

        var decision = store.Handle(new ConfirmOrder(orderId))
            .NoValidation()
            .Decide(command => Decision<Guid>.Succeed(
                command.OrderId,
                new OrderConfirmed { OrderId = command.OrderId }));

        await eventStore.AppendAsync(
            [ToPersist(serializer, new OrderReserved { OrderId = orderId })],
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DcbConflictException>(
            () => decision.Persist(query, expectedPosition: 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistUnconditionally_MakesTheAbsenceOfAConflictCheckExplicit()
    {
        var backend = new InMemoryEventStoreBackend();
        var eventStore = new EventStore(backend);
        var serializer = CreateSerializer();
        var store = new AlbertoStore(eventStore, serializer);
        var orderId = Guid.NewGuid();

        await eventStore.AppendAsync(
            [ToPersist(serializer, new OrderReserved { OrderId = orderId })],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await store.Handle(new ConfirmOrder(orderId))
            .NoValidation()
            .Decide(command => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }))
            .PersistUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, await eventStore.GetLastPositionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithEventsFrom_UsesTheEventStoreFromEachDependencyScope()
    {
        var services = new ServiceCollection();
        const string moduleKey = "tenant-module";
        services.AddKeyedScoped<IEventStore>(moduleKey, (_, _) => new ScopedEventStore());
        services.AddAlberto(moduleKey, builder => builder
            .WithEventsFrom(typeof(AlbertoStore).Assembly));

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        await using var firstScope = provider.CreateAsyncScope();
        var firstStore = firstScope.ServiceProvider.GetRequiredService<AlbertoStore>();
        var firstAdapter = (ScopedEventStore)firstScope.ServiceProvider
            .GetRequiredKeyedService<IEventStore>(moduleKey);

        var firstResult = await firstStore
            .Handle("first")
            .NoValidation()
            .Decide(_ => Decision.Succeed(new ScopeProbe()))
            .PersistUnconditionally(TestContext.Current.CancellationToken);

        await using var secondScope = provider.CreateAsyncScope();
        var secondStore = secondScope.ServiceProvider.GetRequiredService<AlbertoStore>();
        var secondAdapter = (ScopedEventStore)secondScope.ServiceProvider
            .GetRequiredKeyedService<IEventStore>(moduleKey);

        var secondResult = await secondStore
            .Handle("second")
            .NoValidation()
            .Decide(_ => Decision.Succeed(new ScopeProbe()))
            .PersistUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(firstResult.IsSuccess);
        Assert.True(secondResult.IsSuccess);
        Assert.NotSame(firstStore, secondStore);
        Assert.NotSame(firstAdapter, secondAdapter);
        Assert.Equal(1, firstAdapter.AppendCount);
        Assert.Equal(1, secondAdapter.AppendCount);
    }

    private static OrderState Apply(OrderState state, IEvent _)
        => state with { EventCount = state.EventCount + 1 };

    private static EventToPersist ToPersist<TEvent>(EventSerializer serializer, TEvent @event)
        where TEvent : IEvent
        => new()
        {
            EventType = EventType.FromType(@event.GetType()),
            Tags = serializer.ExtractTags(@event),
            EventData = serializer.Serialize(@event),
            Metadata = new Dictionary<string, string>()
        };

    private static EventSerializer CreateSerializer()
    {
        var registry = new Dictionary<string, Type>
        {
            [EventTypeAttribute.GetEventTypeId(typeof(OrderCreated))] = typeof(OrderCreated),
            [EventTypeAttribute.GetEventTypeId(typeof(OrderReserved))] = typeof(OrderReserved),
            [EventTypeAttribute.GetEventTypeId(typeof(OrderConfirmed))] = typeof(OrderConfirmed),
        };

        var ctor = typeof(EventSerializer).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IReadOnlyDictionary<string, Type>), typeof(JsonSerializerOptions)],
            modifiers: null)
            ?? throw new InvalidOperationException("EventSerializer private constructor not found.");

        return (EventSerializer)ctor.Invoke([registry, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }]);
    }

    private sealed class ScopedEventStore : IEventStore
    {
        public int AppendCount { get; private set; }

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events,
            DcbQuery? dcbQuery = null,
            long? expectedPosition = null,
            CancellationToken cancellationToken = default)
        {
            AppendCount++;
            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query,
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }
}
