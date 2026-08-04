namespace Alberto.Testing;

/// <summary>
/// The decision verbs, plus <see cref="ThenState"/> — the base of the two stages a decision made
/// against folded state can land on.
/// </summary>
/// <remarks>
/// A stateless decider never reaches this type, which is the whole point of it being separate:
/// <c>Spec.Stateless().When(...).ThenState(...)</c> does not compile, because there is no state
/// for it to have asserted on.
/// </remarks>
public abstract class StatefulDecisionAssertions<TState, TSelf> : DecisionAssertions<TSelf>
    where TState : new()
    where TSelf : StatefulDecisionAssertions<TState, TSelf>
{
    private readonly Evolver<TState> _evolver;
    private readonly TState _stateBefore;

    private protected StatefulDecisionAssertions(Evolver<TState> evolver, TState stateBefore, Decision decision)
        : base(decision)
    {
        _evolver = evolver;
        _stateBefore = stateBefore;
    }

    /// <summary>
    /// Asserts on the state as it stands after the emitted events are folded in — what the next
    /// command on this slice would see.
    /// </summary>
    /// <remarks>
    /// To assert on the history alone, without a decision, call it one stage earlier:
    /// <c>Spec.For(evolver).Given(events).ThenState(...)</c> is how an evolver is specified
    /// directly.
    /// </remarks>
    public TSelf ThenState(Action<TState> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        if (Decision.IsError)
            throw new SpecificationException(
                "ThenState asserts the state after the emitted events are folded in, but the " +
                "decision failed, so nothing was emitted and the state is unchanged. Assert the " +
                "failure with ThenFails(...) instead.");

        assert(Decision.Events.Aggregate(_stateBefore, _evolver.Evolve));
        return Self;
    }
}
