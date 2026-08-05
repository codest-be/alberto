using Alberto.Subscriptions;

namespace Alberto.Testing;

/// <summary>
/// The opening stage of a projection specification: a declaration and nothing else. Start one with
/// <see cref="ProjectionSpec.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// It sets context, it arranges, or it acts — there is no Then-verb here, so
/// <c>ProjectionSpec.For(declaration).ThenNoDocuments()</c> does not compile. Nothing has been
/// projected, so there is nothing to assert about.
/// </para>
/// <para>
/// One document set, not one per tenant. The runtime keeps a state store per tenant and that
/// store is what isolates them, so tenancy here is context and nothing more:
/// <see cref="ProjectionStage{TState,TSelf}.ForTenant"/> sets the
/// <see cref="ProjectionContext.TenantId"/> a projection may copy into its state, and a
/// specification that means to assert isolation between two tenants is two specifications.
/// </para>
/// </remarks>
public sealed class ProjectionSpecification<TState>
    : ProjectionStage<TState, ProjectionSpecification<TState>>
    where TState : new()
{
    internal ProjectionSpecification(ProjectionDeclaration<TState> declaration)
        : base(new ProjectionFold<TState>(declaration)) { }

    private protected override ProjectionSpecification<TState> Self => this;

    /// <summary>
    /// The history: the events already projected, folded into documents in the order given.
    /// Events the projection declares no handler for are ignored, exactly as they are in
    /// production — which is the usual case, since one log feeds every projection.
    /// </summary>
    public ProjectionHistorySpecification<TState> Given(params IEvent[] events)
    {
        Fold.Arrange(events);
        return new ProjectionHistorySpecification<TState>(Fold);
    }

    /// <summary>
    /// States that nothing has been projected yet. Equivalent to omitting <c>Given</c>, and there
    /// so the empty case reads as a decision rather than an oversight.
    /// </summary>
    public ProjectionHistorySpecification<TState> GivenNoEvents() => Given();

    /// <summary>Projects the events under test onto an empty projection.</summary>
    public ProjectionOutcomeSpecification<TState> When(params IEvent[] events)
    {
        Fold.Act(events);
        return new ProjectionOutcomeSpecification<TState>(Fold);
    }
}
