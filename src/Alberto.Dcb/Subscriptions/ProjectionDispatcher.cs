using System.Reflection;
using System.Text.Json;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Reflection-based dispatcher that routes events to IProject&lt;TState, TEvent&gt; handlers.
/// Discovered automatically from the projection class.
/// </summary>
internal sealed class ProjectionDispatcher<TState>
{
    private readonly Dictionary<string, Handler> _handlers = new();

    private ProjectionDispatcher() { }

    /// <summary>
    /// The event types this projection handles.
    /// </summary>
    public IReadOnlySet<string> HandledEventTypes => _handlers.Keys.ToHashSet();

    /// <summary>
    /// Create a dispatcher for a projection instance.
    /// Scans for IProject&lt;TState, TEvent&gt; implementations.
    /// </summary>
    public static ProjectionDispatcher<TState> For(object projection)
    {
        var dispatcher = new ProjectionDispatcher<TState>();
        var projectionType = projection.GetType();

        // Find all IProject<TState, TEvent> interfaces implemented by the projection
        var projectInterfaces = projectionType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IProject<,>))
            .Where(i => i.GetGenericArguments()[0] == typeof(TState));

        foreach (var iface in projectInterfaces)
        {
            var eventType = iface.GetGenericArguments()[1];
            var eventTypeId = EventTypeAttribute.GetEventTypeId(eventType);

            // Get the Apply method
            var applyMethod = iface.GetMethod(nameof(IProject<TState, IEvent>.Apply));
            if (applyMethod is null) continue;

            // Get the GetDocumentId method
            var getDocIdMethod = iface.GetMethod(nameof(IProject<TState, IEvent>.GetDocumentId));
            if (getDocIdMethod is null) continue;

            dispatcher._handlers[eventTypeId] = new Handler(
                eventType,
                projection,
                applyMethod,
                getDocIdMethod);
        }

        return dispatcher;
    }

    /// <summary>
    /// Apply an event envelope to the state.
    /// </summary>
    public ProjectionResult<TState> Apply(TState state, IEventEnvelope envelope)
    {
        if (!_handlers.TryGetValue(envelope.EventType.Id, out var handler))
            return Projection.Unchanged<TState>();

        return handler.Apply(state, envelope);
    }

    /// <summary>
    /// Get the document ID for an event envelope.
    /// </summary>
    public string GetDocumentId(IEventEnvelope envelope)
    {
        if (!_handlers.TryGetValue(envelope.EventType.Id, out var handler))
            throw new InvalidOperationException(
                $"No handler registered for event type '{envelope.EventType.Id}'");

        return handler.GetDocumentId(envelope);
    }

    private sealed class Handler
    {
        private readonly Type _eventType;
        private readonly object _projection;
        private readonly MethodInfo _applyMethod;
        private readonly MethodInfo _getDocIdMethod;

        public Handler(Type eventType, object projection, MethodInfo applyMethod, MethodInfo getDocIdMethod)
        {
            _eventType = eventType;
            _projection = projection;
            _applyMethod = applyMethod;
            _getDocIdMethod = getDocIdMethod;
        }

        public ProjectionResult<TState> Apply(TState state, IEventEnvelope envelope)
        {
            var @event = DeserializeEvent(envelope);
            var context = ProjectionContext.FromEnvelope(envelope);

            var result = _applyMethod.Invoke(_projection, [state, @event, context]);
            return (ProjectionResult<TState>)result!;
        }

        public string GetDocumentId(IEventEnvelope envelope)
        {
            var @event = DeserializeEvent(envelope);
            var result = _getDocIdMethod.Invoke(_projection, [@event]);
            return (string)result!;
        }

        private object DeserializeEvent(IEventEnvelope envelope)
        {
            return JsonSerializer.Deserialize(envelope.EventData, _eventType)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize event '{envelope.EventType.Id}' to type '{_eventType.Name}'");
        }
    }
}
