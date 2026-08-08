namespace Alberto.Testing;

/// <summary>
/// A stateless decision has been made. The decision verbs, and no <c>ThenState</c>: there was no
/// state to decide against and there is none to assert on.
/// </summary>
public sealed class StatelessDecisionSpecification : DecisionAssertions<StatelessDecisionSpecification>
{
    internal StatelessDecisionSpecification(Decision decision) : base(decision) { }

    private protected override StatelessDecisionSpecification Self => this;
}
