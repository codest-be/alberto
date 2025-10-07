namespace Albert.EventSourcing.Projectors;

/// <summary>
/// Extension methods for projectors.
/// </summary>
public static class ProjectorExtensions
{
    /// <summary>
    /// Evolves a state by applying a sequence of events using a projector.
    /// </summary>
    /// <typeparam name="TState">The state type</typeparam>
    /// <param name="projector">The projector to use</param>
    /// <param name="events">The events to apply</param>
    /// <param name="initialState">Optional initial state (defaults to new instance)</param>
    /// <returns>The final state after applying all events</returns>
    public static TState Evolve<TState>(
        this IProjector<TState> projector,
        IEnumerable<object> events,
        TState? initialState = default) where TState : new()
    {
        var state = initialState ?? new TState();
        return events.Aggregate(state, projector.Apply);
    }
}