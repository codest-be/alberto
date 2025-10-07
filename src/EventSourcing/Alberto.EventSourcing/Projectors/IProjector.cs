namespace Alberto.EventSourcing.Projectors;

/// <summary>
/// Projects events into a state representation.
/// Used for both read model projections and aggregate state reconstruction.
/// </summary>
/// <typeparam name="TState">The state type to project into</typeparam>
public interface IProjector<TState> where TState : new()
{
    /// <summary>
    /// Applies a single event to the current state, producing a new state.
    /// </summary>
    /// <param name="state">The current state</param>
    /// <param name="event">The event to apply</param>
    /// <returns>The new state after applying the event</returns>
    TState Apply(TState state, object @event);
}