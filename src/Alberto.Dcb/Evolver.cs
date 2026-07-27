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
    /// Reconstitute state from a sequence of events using raw JSON deserialization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This overload falls back to <see cref="System.Text.Json.JsonSerializer"/> for every event.
    /// It is suitable when all events in <paramref name="events"/> are at the same schema version
    /// as the handler types declared by this evolver.
    /// </para>
    /// <para>
    /// <b>If any envelope was stored at an older schema version than the handler expects</b>
    /// (i.e. upcasting is required), this overload throws <see cref="InvalidOperationException"/>
    /// rather than silently returning stale state.  Use the serializer-threaded overload instead:
    /// <c>evolver.Reconstitute(envelopes, initial, serializer.Deserialize)</c>, or go through
    /// the command pipeline (<c>CommandPipeline.Load(boundary, evolver)</c>) or
    /// <c>DeciderExtensions.DecideAndAppendAsync</c> with a serializer argument — both thread
    /// <see cref="EventSerializer.Deserialize"/> automatically.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an envelope's stored schema version is less than the version declared on
    /// the corresponding <c>IEvolve&lt;TState, TEvent&gt;</c> handler type. A silent wrong
    /// answer is worse than a loud failure.
    /// </exception>
    public TState Reconstitute(IEnumerable<IEventEnvelope> events, TState? initial = default)
        => events.Aggregate(initial ?? new TState(), Evolve);

    /// <summary>
    /// Evolves state using a caller-supplied deserializer. Called by <c>AlbertoStore</c>
    /// to thread <see cref="EventSerializer.Deserialize"/> (and its upcaster chain) into the
    /// dispatch loop, rather than relying on the raw-JSON fallback.
    /// </summary>
    internal TState Evolve(TState state, IEventEnvelope envelope, Func<IEventEnvelope, object> deserialize)
        => _dispatcher.Evolve(state, envelope, deserialize);

    /// <summary>
    /// Reconstitutes state from a sequence of events, routing each envelope through the
    /// supplied deserializer. Called by <c>AlbertoStore</c> so that every event
    /// passes through <see cref="EventSerializer"/> and any registered upcasters before
    /// reaching the handler.
    /// </summary>
    internal TState Reconstitute(
        IEnumerable<IEventEnvelope> events,
        TState? initial,
        Func<IEventEnvelope, object> deserialize)
        => events.Aggregate(initial ?? new TState(), (s, e) => Evolve(s, e, deserialize));
}
