namespace Alberto.Dcb;

/// <summary>
/// Folds events into state using a handler map — the command-side equivalent of Projection.
/// Implement IEvolve&lt;TState, TEvent&gt; for each event type.
/// </summary>
/// <example>
/// <code>
/// public class OrderEvolver : Evolver&lt;OrderState&gt;,
///     IEvolve&lt;OrderState, OrderCreated&gt;,
///     IEvolve&lt;OrderState, OrderConfirmed&gt;
/// {
///     public OrderState Apply(OrderState state, OrderCreated e)
///         => state with { Status = OrderStatus.Draft };
///
///     public OrderState Apply(OrderState state, OrderConfirmed e)
///         => state with { Status = OrderStatus.Confirmed };
/// }
/// </code>
/// </example>
public abstract class Evolver<TState> where TState : new()
{
    private readonly EvolverDispatcher<TState> _dispatcher;

    protected Evolver()
    {
        _dispatcher = EvolverDispatcher<TState>.For(this);
    }

    /// <summary>
    /// The event types this evolver handles.
    /// </summary>
    public IReadOnlySet<string> HandledEventTypes => _dispatcher.HandledEventTypes;

    /// <summary>
    /// Apply a single event envelope to the state.
    /// </summary>
    public TState Evolve(TState state, IEventEnvelope envelope)
        => _dispatcher.Evolve(state, envelope);

    /// <summary>
    /// Reconstitute state from a sequence of events.
    /// </summary>
    public TState Reconstitute(IEnumerable<IEventEnvelope> events, TState? initial = default)
        => events.Aggregate(initial ?? new TState(), Evolve);
}
