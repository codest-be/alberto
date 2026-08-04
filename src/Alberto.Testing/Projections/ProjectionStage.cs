using Alberto.Subscriptions;

namespace Alberto.Testing;

/// <summary>
/// The context verbs, shared by every stage of a projection specification: what tenant, timestamp,
/// position and metadata the events from here on carry.
/// </summary>
/// <typeparam name="TState">The projection's document type.</typeparam>
/// <typeparam name="TSelf">
/// The concrete stage. Every verb returns it rather than this base, so setting context never moves
/// a specification backwards or forwards through the chain.
/// </typeparam>
/// <remarks>
/// Context is set for what comes next, so it is legal at every stage — between two <c>Given</c>
/// batches, and between two <c>When</c>s, which is how a redelivery at a rewound position is
/// written.
/// </remarks>
public abstract class ProjectionStage<TState, TSelf>
    where TState : new()
    where TSelf : ProjectionStage<TState, TSelf>
{
    private protected ProjectionStage(ProjectionFold<TState> fold) => Fold = fold;

    private protected ProjectionFold<TState> Fold { get; }

    /// <summary>The derived instance, so every verb can return <typeparamref name="TSelf"/>.</summary>
    private protected abstract TSelf Self { get; }

    /// <summary>
    /// The tenant every event from here on carries, and so the
    /// <see cref="ProjectionContext.TenantId"/> the projection sees. Defaults to
    /// <see langword="null"/>, which is what a module without tenancy passes.
    /// </summary>
    public TSelf ForTenant(string? tenantId)
    {
        Fold.ForTenant(tenantId);
        return Self;
    }

    /// <summary>
    /// The timestamp every event from here on carries. Defaults to
    /// <see cref="ProjectionSpec.Epoch"/>; call this between batches when a projection records
    /// times and the test is about which one won.
    /// </summary>
    public TSelf At(DateTimeOffset timestamp)
    {
        Fold.At(timestamp);
        return Self;
    }

    /// <summary>
    /// The global position of the next event; subsequent events continue from there. Positions
    /// otherwise start at 1 and increment, which is what the log would have given them.
    /// </summary>
    /// <remarks>
    /// Rewinding is how a redelivery is written: replay an event at the position it already had
    /// and a state implementing <see cref="IProjectionEntity"/> should ignore it.
    /// </remarks>
    public TSelf AtPosition(long position)
    {
        Fold.AtPosition(position);
        return Self;
    }

    /// <summary>
    /// The metadata every event from here on carries. Defaults to empty.
    /// </summary>
    public TSelf WithMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        Fold.WithMetadata(metadata);
        return Self;
    }
}
