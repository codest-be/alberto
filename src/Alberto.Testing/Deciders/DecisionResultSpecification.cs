namespace Alberto.Testing;

/// <summary>
/// A decision that carries a value has been made against folded state. Everything
/// <see cref="DecisionSpecification{TState}"/> offers, plus <see cref="ThenResult"/>.
/// </summary>
/// <remarks>
/// <see cref="ThenResult"/> lives here and only here, so it cannot be called on a decision that
/// carries no value, and it takes no type argument: the result type came from the
/// <c>Decision&lt;TResult&gt;</c> the <c>When</c> returned, so naming it again could only be a way
/// to get it wrong.
/// </remarks>
public sealed class DecisionResultSpecification<TState, TResult>
    : StatefulDecisionAssertions<TState, DecisionResultSpecification<TState, TResult>>
    where TState : new()
{
    private readonly Decision<TResult> _decision;

    internal DecisionResultSpecification(Evolver<TState> evolver, TState stateBefore, Decision<TResult> decision)
        : base(evolver, stateBefore, decision) => _decision = decision;

    private protected override DecisionResultSpecification<TState, TResult> Self => this;

    /// <summary>Asserts on the value the decision carried.</summary>
    public DecisionResultSpecification<TState, TResult> ThenResult(Action<TResult> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        ThenSucceeds();

        assert(_decision.Value);
        return this;
    }
}
