namespace Alberto.EventStore.Subscriptions.Subscriptions;

/// <summary>
/// Marker interface for typed event handlers
/// </summary>
public interface IEventHandler
{
    // Marker interface - handlers implement IHandleEvent<T>
}

/// <summary>
/// Typed event handler interface
/// </summary>
/// <typeparam name="TEvent">The event type to handle</typeparam>
public interface IHandleEvent<in TEvent> : IEventHandler
    where TEvent : class
{
    /// <summary>
    /// Handles a typed event
    /// </summary>
    ValueTask Handle(TEvent @event, EventContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Marker interface for projection subscriptions.
/// Projection subscriptions are treated specially by EventRouter for batching and optimization.
/// The EventRouter uses reflection to access Projector and Repository properties.
/// </summary>
public interface IProjectionSubscription : IEventHandler
{
    /// <summary>
    /// Extract the projection key(s) from an event.
    /// Can return multiple keys if event affects multiple projections.
    /// </summary>
    IEnumerable<object> GetKeys(object @event);
}