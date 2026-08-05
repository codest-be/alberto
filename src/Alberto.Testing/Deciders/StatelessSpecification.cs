namespace Alberto.Testing;

/// <summary>
/// The opening stage of a stateless decider specification — one that validates its arguments and
/// emits, with no history to consult. Start one with <see cref="Spec.Stateless"/>.
/// </summary>
/// <remarks>
/// There is no <c>Given</c> here, and that is the point: a creation decision that has nothing to
/// fold should not be made to arrange an evolver and an empty state just to be called. Nor is
/// there a <c>ThenState</c> anywhere down this branch of the chain, for the same reason.
/// </remarks>
public sealed class StatelessSpecification
{
    internal StatelessSpecification() { }

    /// <summary>Calls the decider.</summary>
    public StatelessDecisionSpecification When(Func<Decision> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return new StatelessDecisionSpecification(decide());
    }

    /// <summary>
    /// Calls a decider that returns a value alongside its events, landing on the stage that can
    /// assert on it with <see cref="StatelessDecisionResultSpecification{TResult}.ThenResult"/>.
    /// </summary>
    public StatelessDecisionResultSpecification<TResult> When<TResult>(Func<Decision<TResult>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return new StatelessDecisionResultSpecification<TResult>(decide());
    }
}
