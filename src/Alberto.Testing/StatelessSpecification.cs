namespace Alberto.Testing;

/// <summary>
/// A specification for a decider that reads no state — it validates its arguments and emits, with
/// no history to consult. Start one with <see cref="Spec.Stateless"/>.
/// </summary>
/// <remarks>
/// There is no <c>Given</c> here, and that is the point: a creation decision that has nothing to
/// fold should not be made to arrange an evolver and an empty state just to be called.
/// </remarks>
public sealed class StatelessSpecification : SpecificationBase<StatelessSpecification>
{
    internal StatelessSpecification() { }

    private protected override StatelessSpecification Self => this;

    /// <summary>Calls the decider.</summary>
    public StatelessSpecification When(Func<Decision> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        Act(decide());
        return this;
    }

    /// <summary>
    /// Calls a decider that returns a value alongside its events. Assert on the value with
    /// <see cref="SpecificationBase{TSelf}.ThenResult{TResult}"/>.
    /// </summary>
    public StatelessSpecification When<TResult>(Func<Decision<TResult>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        Act(decide());
        return this;
    }
}
