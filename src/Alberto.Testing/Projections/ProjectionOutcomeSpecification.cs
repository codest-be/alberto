namespace Alberto.Testing;

/// <summary>
/// The events under test have been projected. Everything
/// <see cref="ProjectionAssertions{TState,TSelf}"/> offers, plus the two verbs that need a
/// baseline: <see cref="ThenDeleted"/> and <see cref="ThenUnchanged"/>.
/// </summary>
/// <remarks>
/// <c>Given</c> is gone — arrange comes before act, and once a projection has acted there is no
/// history left to add. <c>When</c> is not, because a specification may act more than once:
/// replaying an event at a rewound position is two acts, and each one re-bases what
/// <see cref="ThenUnchanged"/> and <see cref="ThenDeleted"/> compare against.
/// </remarks>
public sealed class ProjectionOutcomeSpecification<TState>
    : ProjectionAssertions<TState, ProjectionOutcomeSpecification<TState>>
    where TState : new()
{
    internal ProjectionOutcomeSpecification(ProjectionFold<TState> fold) : base(fold) { }

    private protected override ProjectionOutcomeSpecification<TState> Self => this;

    /// <summary>
    /// Projects more events under test, re-basing what <see cref="ThenUnchanged"/> and
    /// <see cref="ThenDeleted"/> compare against.
    /// </summary>
    public ProjectionOutcomeSpecification<TState> When(params IEvent[] events)
    {
        Fold.Act(events);
        return this;
    }

    /// <summary>
    /// Asserts that <c>When</c> deleted <paramref name="documentId"/> — that it existed before
    /// and does not now. Distinguishes a document that was removed from one that was never
    /// written, which <see cref="ProjectionAssertions{TState,TSelf}.ThenNoDocument"/> cannot.
    /// </summary>
    public ProjectionOutcomeSpecification<TState> ThenDeleted(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (!Fold.Before.ContainsKey(documentId))
            throw new SpecificationException(
                $"Expected When(...) to delete '{documentId}', but no such document existed " +
                "before it. Arrange the document with Given(...) first.");

        if (Fold.Documents.ContainsKey(documentId))
            throw new SpecificationException(
                $"Expected When(...) to delete '{documentId}', but the projection still holds it.");

        return this;
    }

    /// <summary>
    /// Asserts that <c>When</c> wrote nothing — every event it carried was one this projection
    /// does not handle, or was routed to no document, or was applied to <c>Unchanged</c>.
    /// </summary>
    public ProjectionOutcomeSpecification<TState> ThenUnchanged()
    {
        if (Fold.Written.Count > 0 || Fold.Removed.Count > 0)
            throw new SpecificationException(
                "Expected When(...) to leave the projection untouched, but it wrote " +
                $"{Names(Fold.Written)} and deleted {Names(Fold.Removed)}.");

        return this;
    }
}
