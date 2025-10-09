using System.Text.Json;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.EventStore.InMemory;
using Alberto.EventStore.MultiTenant;
using Alberto.Example.ComponentTests.Steps.Orders;
using Alberto.Example.Modules.Orders;

namespace Alberto.Example.ComponentTests;

public sealed class UseCase
{
    private readonly InMemoryEventStoreBackend _eventStore;
    private readonly List<Guid> _givenEventIds = [];
    private readonly HttpClient _httpClient;
    private readonly Tenant _tenant;

    internal UseCase(InMemoryEventStoreBackend eventStore, HttpClient httpClient, Tenant tenant)
    {
        _eventStore = eventStore;
        _httpClient = httpClient;
        _tenant = tenant;
    }

    public UseCase Given(Guid orderId, params object[] events)
    {
        var eventsToPersist = events.Select(evt => ToEventToPersist(evt, orderId)).ToList();

        var streamQuery = new StreamQuery([new EventTag(Tags.Order, orderId.ToString())]);

        var persisted = _eventStore.Append(_tenant, eventsToPersist, streamQuery, null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        _givenEventIds.AddRange(persisted.Select(e => e.Id));

        return this;
    }

    public CommandAsserter When(CreateOrderStep step)
    {
        var result = step.ExecuteAsync(_httpClient, CancellationToken.None).GetAwaiter().GetResult();
        return new CommandAsserter(result!, _givenEventIds.ToArray(), _eventStore);
    }

    public CommandAsserter When(PlaceOrderStep step)
    {
        var result = step.ExecuteAsync(_httpClient, CancellationToken.None).GetAwaiter().GetResult();
        return new CommandAsserter(result!, _givenEventIds.ToArray(), _eventStore);
    }

    public CommandAsserter When(CancelOrderStep step)
    {
        var result = step.ExecuteAsync(_httpClient, CancellationToken.None).GetAwaiter().GetResult();
        return new CommandAsserter(result!, _givenEventIds.ToArray(), _eventStore);
    }

    public CommandAsserter When(ShipOrderStep step)
    {
        var result = step.ExecuteAsync(_httpClient, CancellationToken.None).GetAwaiter().GetResult();
        return new CommandAsserter(result!, _givenEventIds.ToArray(), _eventStore);
    }

    private static IEventToPersist ToEventToPersist(object evt, Guid orderId)
    {
        var eventType = EventType.GetEventType(evt.GetType());
        if (eventType == null)
        {
            throw new InvalidOperationException(
                $"Event type {evt.GetType().Name} does not have an [EventType] attribute");
        }

        return new EventToPersist
        {
            EventType = eventType,
            EventJson = JsonSerializer.Serialize(evt),
            Tags = [new EventTag(Tags.Order, orderId.ToString())],
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };
    }
}