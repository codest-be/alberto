using System.Reflection;
using System.Text.Json;
using Alberto.Dcb.InMemory;
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
        var eventStore = new InMemoryEventStore(backend);
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
        var eventStore = new InMemoryEventStore(backend);
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
}
