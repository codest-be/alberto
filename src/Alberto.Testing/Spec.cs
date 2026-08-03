namespace Alberto.Testing;

/// <summary>
/// The entry point for decider specifications — Given/When/Then over a decision function,
/// with no event store, no host and no infrastructure.
/// </summary>
/// <remarks>
/// A decider is a pure function of state, and its state is a pure fold of events. That makes it
/// the one part of a slice testable without arranging anything: hand it the events that happened,
/// call it, and assert on what it decided.
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
