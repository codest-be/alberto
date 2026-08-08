namespace Alberto.Testing;

/// <summary>
/// A stateless decision that carries a value has been made. Everything
/// <see cref="StatelessDecisionSpecification"/> offers, plus <see cref="ThenResult"/>.
/// </summary>
public sealed class StatelessDecisionResultSpecification<TResult>
    : DecisionAssertions<StatelessDecisionResultSpecification<TResult>>
{
    private readonly Decision<TResult> _decision;

    internal StatelessDecisionResultSpecification(Decision<TResult> decision) : base(decision) =>
        _decision = decision;

    private protected override StatelessDecisionResultSpecification<TResult> Self => this;

    /// <summary>Asserts on the value the decision carried.</summary>
    public StatelessDecisionResultSpecification<TResult> ThenResult(Action<TResult> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        ThenSucceeds();

        assert(_decision.Value);
        return this;
    }
}
