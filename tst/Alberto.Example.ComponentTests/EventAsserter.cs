using System.Text.Json;
using Alberto.EventStore.Events;
using Alberto.EventStore.InMemory;
using Xunit;

namespace Alberto.Example.ComponentTests;

public sealed class EventAsserter
{
    private readonly InMemoryEventStoreBackend _eventStore;
    private readonly Guid[] _givenEventIds;

    internal EventAsserter(InMemoryEventStoreBackend eventStore, Guid[] givenEventIds)
    {
        _eventStore = eventStore;
        _givenEventIds = givenEventIds;
    }

    /// <summary>
    /// Get new events (excluding Given events)
    /// </summary>
    private IEnumerable<IEventEnvelope> NewEvents =>
        _eventStore.Events.Where(e => !_givenEventIds.Contains(e.Id));

    public EventAsserter AssertEvent<TEvent>()
    {
        var eventType = EventType.GetEventType(typeof(TEvent));
        if (eventType == null)
        {
            throw new InvalidOperationException(
                $"Event type {typeof(TEvent).Name} does not have an [EventType] attribute");
        }

        var hasEvent = NewEvents.Any(e => e.EventType.Id == eventType.Id);

        Assert.True(hasEvent,
            $"Expected event of type {eventType.Id} but it was not persisted. " +
            $"Persisted events: {string.Join(", ", NewEvents.Select(e => e.EventType.Id))}");

        return this;
    }

    public EventAsserter AssertEvent<TEvent>(Action<TEvent> assertion)
    {
        var eventType = EventType.GetEventType(typeof(TEvent));
        if (eventType == null)
        {
            throw new InvalidOperationException(
                $"Event type {typeof(TEvent).Name} does not have an [EventType] attribute");
        }

        var envelope = NewEvents.FirstOrDefault(e => e.EventType.Id == eventType.Id);

        Assert.NotNull(envelope);

        var evt = JsonSerializer.Deserialize<TEvent>(envelope.EventJson);
        Assert.NotNull(evt);

        assertion(evt);

        return this;
    }

    public EventAsserter AssertEventCount(int expectedCount)
    {
        var actualCount = NewEvents.Count();
        Assert.Equal(expectedCount, actualCount);
        return this;
    }

    public EventAsserter AssertNoEvents()
    {
        return AssertEventCount(0);
    }
}