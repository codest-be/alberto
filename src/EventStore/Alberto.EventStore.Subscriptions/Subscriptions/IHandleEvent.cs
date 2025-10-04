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