using System.Reflection;
using System.Text.Json;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Reflection-based dispatcher that routes events to IReact&lt;TEvent&gt; handlers.
/// Discovered automatically from the reactor class.
/// </summary>
internal sealed class ReactorDispatcher
{
    private readonly Dictionary<string, Handler> _handlers = new();

    private ReactorDispatcher() { }

    /// <summary>
    /// The event types this reactor handles.
    /// </summary>
    public IReadOnlySet<string> HandledEventTypes => _handlers.Keys.ToHashSet();

    /// <summary>
    /// Create a dispatcher for a reactor instance.
    /// Scans for IReact&lt;TEvent&gt; implementations.
    /// </summary>
    public static ReactorDispatcher For(object reactor)
    {
        var dispatcher = new ReactorDispatcher();
        var reactorType = reactor.GetType();

        // Find all IReact<TEvent> interfaces implemented by the reactor
        var reactInterfaces = reactorType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReact<>));

        foreach (var iface in reactInterfaces)
        {
            var eventType = iface.GetGenericArguments()[0];
            var eventTypeId = EventTypeAttribute.GetEventTypeId(eventType);

            // Get the ReactAsync method
            var reactMethod = iface.GetMethod(nameof(IReact<IEvent>.ReactAsync));
            if (reactMethod is null) continue;

            dispatcher._handlers[eventTypeId] = new Handler(eventType, reactor, reactMethod);
        }

        return dispatcher;
    }

    /// <summary>
    /// React to an event envelope.
    /// </summary>
    public async Task ReactAsync(IEventEnvelope envelope, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(envelope.EventType.Id, out var handler))
            return; // No handler for this event type

        await handler.ReactAsync(envelope, ct);
    }

    /// <summary>
    /// Check if this dispatcher can handle an event type.
    /// </summary>
    public bool CanHandle(string eventTypeId) => _handlers.ContainsKey(eventTypeId);

    private sealed class Handler(Type eventType, object reactor, MethodInfo reactMethod)
    {
        public async Task ReactAsync(IEventEnvelope envelope, CancellationToken ct)
        {
            var @event = DeserializeEvent(envelope);
            var task = (Task)reactMethod.Invoke(reactor, [@event, envelope, ct])!;
            await task;
        }

        private object DeserializeEvent(IEventEnvelope envelope)
        {
            return JsonSerializer.Deserialize(envelope.EventData, eventType)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize event '{envelope.EventType.Id}' to type '{eventType.Name}'");
        }
    }
}
