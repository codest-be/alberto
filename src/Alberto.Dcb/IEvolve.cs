namespace Alberto.Dcb;

/// <summary>
/// Handler for evolving state from a specific event type.
/// Implement this interface for each event type your Evolver handles.
/// </summary>
public interface IEvolve<TState, in TEvent> where TEvent : IEvent
{
    /// <summary>
    /// Applies the event to the current state, returning the new state.
    /// </summary>
    TState Apply(TState state, TEvent @event);
}
