using Alberto.Subscriptions;

namespace Alberto.Testing;

/// <summary>
/// The entry point for projection specifications — Given/When/Then over a
/// <see cref="ProjectionDeclaration{TState}"/>, with no state store, no database and no host.
/// </summary>
/// <remarks>
/// <para>
/// A projection is two pure functions per event — which document it belongs to, and what that
/// document becomes — and a declaration is nothing but those functions. So the interesting part
/// of a projection is testable the same way a decider is: hand it the events that happened and
/// assert on the documents that result.
/// </para>
/// <para>
/// Whether those documents end up in a JSONB column, an EF entity or nowhere at all is the state
/// store's business, and a state store is not involved here. The same specification runs against
/// <c>OrdersOverviewProjection.Declaration</c> (JSONB) and <c>OrderSummaryEfProjection.Declaration</c>
/// (Entity Framework) without either knowing the difference, because the difference is storage
/// and this asserts on projection.
/// </para>
/// <para>
/// Given, When and Then are three stages and three types, not one type with three kinds of method,
/// so the order is the compiler's business rather than a runtime check. There are no Then-verbs
/// before anything has been projected, no <c>Given</c> after <c>When</c>, and no
/// <c>ThenDeleted</c> or <c>ThenUnchanged</c> until there is a <c>When</c> for them to compare
/// across.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// ProjectionSpec.For(OrdersOverviewProjection.Declaration)
///     .Given(new OrderCreated(orderId, customerId, items, null))
///     .When(new OrderConfirmed(orderId, now))
///     .ThenDocument(OrdersOverviewProjection.DocumentId, d =>
///     {
///         d.DraftOrders.Should().Be(0);
///         d.ConfirmedOrders.Should().Be(1);
///     });
/// </code>
/// </example>
public static class ProjectionSpec
{
    /// <summary>
    /// The timestamp every event carries unless <see cref="ProjectionStage{TState,TSelf}.At"/>
    /// says otherwise. Fixed rather than "now" so that a projection writing
    /// <c>ctx.Timestamp</c> into its state can be asserted against a literal.
    /// </summary>
    public static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Starts a specification over <paramref name="declaration"/>. The state type is inferred
    /// from the declaration, so it never has to be named.
    /// </summary>
    public static ProjectionSpecification<TState> For<TState>(ProjectionDeclaration<TState> declaration)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return new ProjectionSpecification<TState>(declaration);
    }
}
