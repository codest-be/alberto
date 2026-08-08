namespace Alberto.Testing;

/// <summary>
/// The entry point for decider specifications — Given/When/Then over a decision function,
/// with no event store, no host and no infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// A decider is a pure function of state, and its state is a pure fold of events. That makes it
/// the one part of a slice testable without arranging anything: hand it the events that happened,
/// call it, and assert on what it decided.
/// </para>
/// <para>
/// Given, When and Then are three stages and three types, not one type with three kinds of method,
/// so the order is the compiler's business rather than a runtime check. A Then-verb that reads a
/// decision does not exist before <c>When</c>; <c>Given</c> does not exist after it; and
/// <c>ThenResult</c> exists only on the stage a <c>Decision&lt;TResult&gt;</c> lands on. What the
/// chain still cannot know is the outcome — <c>ThenState</c> on a decision that failed is a
/// runtime error, because whether it failed is what the specification is there to find out.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// Spec.For(new ConfirmOrderEvolver())
///     .Given(new OrderCreated(orderId, customerId, items, null))
///     .When(state => ConfirmOrderDecider.Decide(state, now))
///     .ThenEmitsOnly&lt;OrderConfirmed&gt;(e => e.OrderId == orderId)
///     .ThenState(s => s.Status.Should().Be(OrderStatus.Confirmed));
///
/// Spec.For(new ShipOrderEvolver())
///     .GivenNoEvents()
///     .When(state => ShipOrderDecider.Decide(state, "TRACK-1", now))
///     .ThenFails(OrderProblems.NotFound());
/// </code>
/// </example>
public static class Spec
{
    /// <summary>
    /// Starts a specification whose state is folded by <paramref name="evolver"/>. The state type
    /// is inferred, so it never has to be named.
    /// </summary>
    /// <remarks>
    /// The opening stage arranges or acts. It has no Then-verbs, so a specification cannot assert
    /// before it has given the compiler something to assert about.
    /// </remarks>
    public static Specification<TState> For<TState>(Evolver<TState> evolver) where TState : new()
    {
        ArgumentNullException.ThrowIfNull(evolver);
        return new Specification<TState>(evolver);
    }

    /// <summary>
    /// Starts a specification for a decider that reads no state — a creation decision, typically,
    /// which validates its arguments and has no history to consult.
    /// </summary>
    /// <example>
    /// <code>
    /// Spec.Stateless()
    ///     .When(() => CreateOrderDecider.Decide(orderId, customerId, items, null, now))
    ///     .ThenEmitsOnly&lt;OrderCreated&gt;();
    /// </code>
    /// </example>
    public static StatelessSpecification Stateless() => new();
}
