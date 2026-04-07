using System.Text.Json;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// A reactor that runs synchronously during event append.
/// Delegates event handling to a function, similar to <see cref="FunctionalReactor{TEvent}"/>
/// but implements <see cref="IPostAppendHandler"/> instead of <see cref="IEventProcessor"/>.
/// </summary>
internal sealed class SyncReactor<TEvent>(Func<TEvent, CancellationToken, Task> handler) : IPostAppendHandler
    where TEvent : class, IEvent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

            var payload = JsonSerializer.Deserialize<TEvent>(@event.EventData, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize event '{@event.EventType.Id}' to '{typeof(TEvent).Name}'.");

            await handler(payload, ct);
        }
    }
}
