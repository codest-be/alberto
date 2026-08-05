namespace Alberto.Testing;

/// <summary>
/// History has been given. From here a specification either acts — <c>When(...)</c> — or asserts
/// on the fold itself with <see cref="ThenState"/>.
/// </summary>
/// <remarks>
/// The decision verbs are not here, because there is no decision yet:
/// <c>Spec.For(evolver).Given(events).ThenSucceeds()</c> does not compile. Neither does seeding a
/// hand-built state at this point — <c>Given(TState, ...)</c> is on the opening stage only, since
/// replacing the state after events have already been folded onto it would silently throw them
/// away.
/// </remarks>
public sealed class HistorySpecification<TState> where TState : new()
{
    private readonly Evolver<TState> _evolver;
    private TState _state;

    internal HistorySpecification(Evolver<TState> evolver, TState state)
    {
        _evolver = evolver;
        _state = state;
    }

    /// <summary>
    /// More history, folded on in the order given. Events the evolver declares no handler for are
    /// ignored, exactly as they are in production.
    /// </summary>
    /// <remarks>
    /// Repeated calls accumulate, so a shared arrange helper can hand back a partly-built
    /// specification and the test can add the events that make its case.
    /// </remarks>
    public HistorySpecification<TState> Given(params IEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var @event in events)
            _state = _evolver.Evolve(_state, @event);

        return this;
    }

    /// <summary>
    /// Asserts on the state the history folded to, with no decision involved. This is how an
    /// evolver is specified directly.
    /// </summary>
    /// <remarks>
    /// An evolver is otherwise covered through the deciders that read it, and that is usually
    /// enough — reach for this when a fold is worth pinning on its own, as a counter or a
    /// collection that several events contribute to tends to be.
    /// </remarks>
    public HistorySpecification<TState> ThenState(Action<TState> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(_state);
        return this;
    }

    /// <summary>Calls the decider with the state folded so far.</summary>
    public DecisionSpecification<TState> When(Func<TState, Decision> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return new DecisionSpecification<TState>(_evolver, _state, decide(_state));
    }

    /// <summary>
    /// Calls a decider that returns a value alongside its events, landing on the stage that can
    /// assert on it with <see cref="DecisionResultSpecification{TState,TResult}.ThenResult"/>.
    /// </summary>
    public DecisionResultSpecification<TState, TResult> When<TResult>(Func<TState, Decision<TResult>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return new DecisionResultSpecification<TState, TResult>(_evolver, _state, decide(_state));
    }
}
