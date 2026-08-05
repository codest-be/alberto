namespace Alberto.Testing;

/// <summary>
/// The opening stage of a decider specification: an evolver and nothing else. Start one with
/// <see cref="Spec.For{TState}"/>.
/// </summary>
/// <remarks>
/// It arranges or it acts — there is no Then-verb here, so <c>Spec.For(evolver).ThenState(...)</c>
/// does not compile. There is nothing to assert on yet: no history has been given and no decision
/// has been made, and asserting on <c>new TState()</c> only ever specifies the compiler.
/// </remarks>
public sealed class Specification<TState> where TState : new()
{
    private readonly Evolver<TState> _evolver;

    internal Specification(Evolver<TState> evolver) => _evolver = evolver;

    /// <summary>
    /// The history: the events that already happened, folded into state in the order given.
    /// Events the evolver declares no handler for are ignored, exactly as they are in production.
    /// </summary>
    public HistorySpecification<TState> Given(params IEvent[] events) =>
        new HistorySpecification<TState>(_evolver, new TState()).Given(events);

    /// <summary>
    /// The history, starting from a state you built by hand rather than from
    /// <c>new TState()</c> — for the cases where arranging through events is the long way round.
    /// </summary>
    /// <remarks>
    /// Only here, on the opening stage. Once events have been folded there is no honest meaning
    /// for a seed, so the stage <c>Given</c> returns does not offer this overload.
    /// </remarks>
    public HistorySpecification<TState> Given(TState initialState, params IEvent[] events) =>
        new HistorySpecification<TState>(_evolver, initialState).Given(events);

    /// <summary>
    /// States that nothing has happened yet. Equivalent to omitting <c>Given</c>, and there so
    /// the empty case reads as a decision rather than an oversight — most "not found" tests are
    /// this case, and silence is a poor way to spell it.
    /// </summary>
    public HistorySpecification<TState> GivenNoEvents() => new(_evolver, new TState());

    /// <summary>Calls the decider against an empty history.</summary>
    public DecisionSpecification<TState> When(Func<TState, Decision> decide) =>
        GivenNoEvents().When(decide);

    /// <summary>
    /// Calls a decider that returns a value alongside its events against an empty history.
    /// </summary>
    public DecisionResultSpecification<TState, TResult> When<TResult>(Func<TState, Decision<TResult>> decide) =>
        GivenNoEvents().When(decide);
}
