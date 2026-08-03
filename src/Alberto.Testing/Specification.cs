namespace Alberto.Testing;

/// <summary>
/// A specification for a decider that reads state — the state its slice's
/// <see cref="Evolver{TState}"/> folds out of the events you hand to <c>Given</c>.
/// Start one with <see cref="Spec.For{TState}"/>.
/// </summary>
public sealed class Specification<TState> : SpecificationBase<Specification<TState>>
    where TState : new()
{
    private readonly Evolver<TState> _evolver;
    private TState _state;
    private TState _stateAfter;
    private bool _acted;

    internal Specification(Evolver<TState> evolver)
    {
        _evolver = evolver;
        _state = new TState();
        _stateAfter = _state;
    }

    private protected override Specification<TState> Self => this;

    /// <summary>
    /// The history: the events that already happened, folded into state in the order given.
    /// Events the evolver declares no handler for are ignored, exactly as they are in production.
    /// </summary>
    /// <remarks>
    /// Repeated calls accumulate, so a shared arrange helper can hand back a partly-built
    /// specification and the test can add the events that make its case.
    /// </remarks>
    public Specification<TState> Given(params IEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        RequireNotActed();

        foreach (var @event in events)
            _state = _evolver.Evolve(_state, @event);

        _stateAfter = _state;
        return this;
    }

    /// <summary>
    /// The history, starting from a state you built by hand rather than from
    /// <c>new TState()</c> — for the cases where arranging through events is the long way round.
    /// </summary>
    public Specification<TState> Given(TState initialState, params IEvent[] events)
    {
        RequireNotActed();
        _state = initialState;
        _stateAfter = _state;
        return Given(events);
    }

    /// <summary>
    /// States that nothing has happened yet. Equivalent to omitting <c>Given</c>, and there so
    /// the empty case reads as a decision rather than an oversight — most "not found" tests are
    /// this case, and silence is a poor way to spell it.
    /// </summary>
    public Specification<TState> GivenNoEvents()
    {
        RequireNotActed();
        return this;
    }

    /// <summary>Calls the decider with the state folded so far.</summary>
    public Specification<TState> When(Func<TState, Decision> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);

        var decision = decide(_state);
        Act(decision);
        Settle(decision);
        return this;
    }

    /// <summary>
    /// Calls a decider that returns a value alongside its events. Assert on the value with
    /// <see cref="SpecificationBase{TSelf}.ThenResult{TResult}"/>.
    /// </summary>
    public Specification<TState> When<TResult>(Func<TState, Decision<TResult>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);

        var decision = decide(_state);
        Act(decision);
        Settle(decision);
        return this;
    }

    /// <summary>
    /// Asserts on the state as it stands after the emitted events are folded in — what the next
    /// command on this slice would see.
    /// </summary>
    public Specification<TState> ThenState(Action<TState> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        if (Decision.IsError)
            throw new SpecificationException(
                "ThenState asserts the state after the emitted events are folded in, but the " +
                "decision failed, so nothing was emitted and the state is unchanged. Assert the " +
                "failure with ThenFails(...) instead.");

        assert(_stateAfter);
        return this;
    }

    private void Settle(Decision decision)
    {
        _acted = true;
        _stateAfter = decision.IsSuccess
            ? decision.Events.Aggregate(_state, _evolver.Evolve)
            : _state;
    }

    private void RequireNotActed()
    {
        if (_acted)
            throw new SpecificationException(
                "Given(...) sets up the history a decision is made against, so it has to come " +
                "before When(...).");
    }
}
