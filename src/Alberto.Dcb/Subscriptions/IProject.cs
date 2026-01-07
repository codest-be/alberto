namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Typed projection handler interface. Implement this for each event type
/// that your projection handles.
/// </summary>
/// <typeparam name="TState">The projection state type.</typeparam>
/// <typeparam name="TEvent">The event type to handle.</typeparam>
public interface IProject<TState, in TEvent> where TEvent : IEvent
{
    /// <summary>
    /// Get the document ID for this event. Used to group events by document.
    /// </summary>
    string GetDocumentId(TEvent @event);

    /// <summary>
    /// Apply the event to the current state and return the result.
    /// Return the new state directly (implicit Set), or use Projection.Delete/Unchanged.
    /// </summary>
    ProjectionResult<TState> Apply(TState state, TEvent @event, ProjectionContext context);
}
