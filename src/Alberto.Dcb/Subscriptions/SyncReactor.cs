namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// A reactor that runs synchronously during event append.
/// Delegates event handling to a function, similar to <see cref="FunctionalReactor{TEvent}"/>
/// but implements <see cref="IPostAppendHandler"/> instead of <see cref="IEventProcessor"/>.
/// </summary>
internal sealed class SyncReactor<TEvent>(
    Func<TEvent, ReactorContext, CancellationToken, Task> handler,
    EventSerializer? serializer = null) : IPostAppendHandler
    where TEvent : class, IEvent
{
    public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        EventTypeAttribute.GetEventTypeId(typeof(TEvent))
    };

    public async Task ProcessAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct)
    {
        foreach (var @event in events)
        {
            if (!HandledEventTypes.Contains(@event.EventType.Id))
                continue;

            var payload = EventEnvelopeExtensions.DeserializeEvent<TEvent>(@event, serializer);
            await handler(payload, ReactorContext.FromEnvelope(@event), ct);
        }
    }
}
