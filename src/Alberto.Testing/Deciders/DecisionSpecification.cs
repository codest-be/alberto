namespace Alberto.Testing;

/// <summary>
/// A decision has been made against folded state. This is the assert stage: every Then-verb is
/// here, and nothing that would arrange or act again is.
/// </summary>
/// <remarks>
/// Reached only through <c>When(state =&gt; ...)</c>, and it offers no <c>Given</c> and no second
/// <c>When</c> — a specification arranges once, acts once, and then asserts as often as it likes.
/// </remarks>
public sealed class DecisionSpecification<TState> : StatefulDecisionAssertions<TState, DecisionSpecification<TState>>
    where TState : new()
{
    internal DecisionSpecification(Evolver<TState> evolver, TState stateBefore, Decision decision)
        : base(evolver, stateBefore, decision) { }

    private protected override DecisionSpecification<TState> Self => this;
}
