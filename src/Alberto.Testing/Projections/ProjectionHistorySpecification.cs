namespace Alberto.Testing;

/// <summary>
/// History has been projected. From here a specification either acts — <c>When(...)</c> — or
/// asserts on the fold itself, which is how a projection's handlers are specified without an act
/// to point at.
/// </summary>
/// <remarks>
/// <c>ThenDeleted</c> and <c>ThenUnchanged</c> are not reachable here, because both compare
/// against how the documents stood before the events under test and there are no events under
/// test yet.
/// </remarks>
public sealed class ProjectionHistorySpecification<TState>
    : ProjectionAssertions<TState, ProjectionHistorySpecification<TState>>
    where TState : new()
{
    internal ProjectionHistorySpecification(ProjectionFold<TState> fold) : base(fold) { }

    private protected override ProjectionHistorySpecification<TState> Self => this;

    /// <summary>
    /// More history, folded on in the order given.
    /// </summary>
    /// <remarks>
    /// Repeated calls accumulate, so a shared arrange helper can hand back a partly-built
    /// specification and the test can add the events that make its case.
    /// </remarks>
    public ProjectionHistorySpecification<TState> Given(params IEvent[] events)
    {
        Fold.Arrange(events);
        return this;
    }

    /// <summary>
    /// Projects the events under test onto the documents built by <c>Given</c>.
    /// </summary>
    public ProjectionOutcomeSpecification<TState> When(params IEvent[] events)
    {
        Fold.Act(events);
        return new ProjectionOutcomeSpecification<TState>(Fold);
    }
}
