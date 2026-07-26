using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace Alberto.Dcb;

/// <summary>
/// Reflection-based dispatcher that routes events to IEvolve&lt;TState, TEvent&gt; handlers.
/// </summary>
internal sealed class EvolverDispatcher<TState>
{
    private readonly Dictionary<string, Handler> _handlers = new();
    private FrozenSet<string> _handledEventTypes = FrozenSet<string>.Empty;

    private EvolverDispatcher() { }

    public IReadOnlySet<string> HandledEventTypes => _handledEventTypes;

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

        dispatcher._handledEventTypes = dispatcher._handlers.Keys.ToFrozenSet(StringComparer.Ordinal);
        return dispatcher;
    }

    public TState Evolve(TState state, IEventEnvelope envelope)
    {
        if (!_handlers.TryGetValue(envelope.EventType.Id, out var handler))
            return state; // Unhandled events leave state unchanged

        return handler.Evolve(state, envelope, deserialize: null);
    }

    /// <summary>
    /// Evolves state using a caller-supplied deserializer so that events pass through
    /// <see cref="EventSerializer"/> (and its upcaster chain) before the handler sees them,
    /// instead of falling back to raw <see cref="System.Text.Json.JsonSerializer"/>.
    /// Called by <see cref="Evolver{TState}"/>'s internal overload, which is in turn called
    /// by <c>AlbertoStore</c>.
    /// </summary>
    internal TState Evolve(TState state, IEventEnvelope envelope, Func<IEventEnvelope, object> deserialize)
    {
        if (!_handlers.TryGetValue(envelope.EventType.Id, out var handler))
            return state;

        return handler.Evolve(state, envelope, deserialize);
    }

    private sealed class Handler(Type eventType, object evolver, MethodInfo applyMethod)
    {
        // The schema version the CLR event type declares — read once and cached.
        // Used to guard against silently deserialising a stale-version envelope through
        // raw JSON when the caller should have threaded a serializer/upcaster chain.
        private readonly int _declaredVersion =
            EventTypeAttribute.GetEventType(eventType)?.Version ?? 1;

        /// <param name="state">Current state.</param>
        /// <param name="envelope">The event envelope to apply.</param>
        /// <param name="deserialize">
        /// When non-null, used in place of raw <see cref="System.Text.Json.JsonSerializer"/> —
        /// the caller passes <see cref="EventSerializer.Deserialize"/> so the upcaster chain
        /// fires before the handler sees the event.
        /// When null, falls back to plain JSON deserialization (standalone / direct-Evolve usage).
        /// Throws <see cref="InvalidOperationException"/> when null and the envelope version is
        /// older than <see cref="_declaredVersion"/>, because raw deserialization in that case
        /// silently produces stale state instead of the correctly upcasted shape.
        /// </param>
        public TState Evolve(TState state, IEventEnvelope envelope, Func<IEventEnvelope, object>? deserialize)
        {
            object @event;

            if (deserialize is not null)
            {
                @event = deserialize(envelope);

                // Verify the upcaster chain terminated at the type this handler declared.
                // A mismatch means IEvolve<TState, TActual> disagrees with what the chain
                // produces — surface that clearly rather than letting Invoke throw an
                // obscure TargetException.
                if (!eventType.IsInstanceOfType(@event))
                    throw new InvalidOperationException(
                        $"Upcaster for '{envelope.EventType.Id}' produced '{@event.GetType().Name}' " +
                        $"but this evolver handler expects '{eventType.Name}'. " +
                        "Ensure the upcaster chain terminates at the type declared in IEvolve<TState, TEvent>.");
            }
            else
            {
                // Guard: if the stored event version is older than the version the handler's
                // CLR type declares, raw JSON deserialization would produce a stale shape —
                // silently returning wrong state instead of the upcasted type.  Fail loud.
                if (envelope.EventType.Version < _declaredVersion)
                    throw new InvalidOperationException(
                        $"Event '{envelope.EventType.Id}' is stored at schema version " +
                        $"{envelope.EventType.Version} but this evolver handler expects version " +
                        $"{_declaredVersion}. Raw JSON deserialization would produce stale state. " +
                        "Supply an EventSerializer so the upcaster chain runs before reconstitution: " +
                        "call evolver.Reconstitute(envelopes, initial, serializer.Deserialize), " +
                        "use CommandPipeline.Load(boundary, evolver) with an AlbertoStore that has " +
                        "the serializer, or use DeciderExtensions.DecideAndAppendAsync with the " +
                        "serializer overload.");

                @event = JsonSerializer.Deserialize(envelope.EventData, eventType)
                    ?? throw new InvalidOperationException(
                        $"Failed to deserialize event '{envelope.EventType.Id}' to type '{eventType.Name}'");
            }

            var result = applyMethod.Invoke(evolver, [state, @event]);
            return (TState)result!;
        }
    }
}
