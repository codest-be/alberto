using System.Reflection;
using System.Text.Json;

namespace Alberto.Dcb;

/// <summary>
/// Reflection-based dispatcher that routes events to IEvolve&lt;TState, TEvent&gt; handlers.
/// </summary>
internal sealed class EvolverDispatcher<TState>
{
    private readonly Dictionary<string, Handler> _handlers = new();

    private EvolverDispatcher() { }

    public IReadOnlySet<string> HandledEventTypes => _handlers.Keys.ToHashSet();

    public static EvolverDispatcher<TState> For(object evolver)
    {
        var dispatcher = new EvolverDispatcher<TState>();
        var evolverType = evolver.GetType();

        var evolveInterfaces = evolverType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEvolve<,>))
            .Where(i => i.GetGenericArguments()[0] == typeof(TState));

        foreach (var iface in evolveInterfaces)
        {
            var eventType = iface.GetGenericArguments()[1];
            var eventTypeId = EventTypeAttribute.GetEventTypeId(eventType);
            var applyMethod = iface.GetMethod(nameof(IEvolve<TState, IEvent>.Apply));
            if (applyMethod is null) continue;

            dispatcher._handlers[eventTypeId] = new Handler(eventType, evolver, applyMethod);
        }

        return dispatcher;
    }

    public TState Evolve(TState state, IEventEnvelope envelope)
    {
        if (!_handlers.TryGetValue(envelope.EventType.Id, out var handler))
            return state; // Unhandled events leave state unchanged

        return handler.Evolve(state, envelope);
    }

    private sealed class Handler(Type eventType, object evolver, MethodInfo applyMethod)
    {
        public TState Evolve(TState state, IEventEnvelope envelope)
        {
            var @event = JsonSerializer.Deserialize(envelope.EventData, eventType)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize event '{envelope.EventType.Id}' to type '{eventType.Name}'");

            var result = applyMethod.Invoke(evolver, [state, @event]);
            return (TState)result!;
        }
    }
}
