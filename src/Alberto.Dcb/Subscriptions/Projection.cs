#pragma warning disable CS0618 // Type or member is obsolete — intentional, this class itself is obsolete infrastructure
namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Pure projection definition. Testable without storage or I/O.
/// Implement IProject&lt;TState, TEvent&gt; for each event type you handle.
/// </summary>
/// <typeparam name="TState">The projection state type. Must have a parameterless constructor.</typeparam>
/// <example>
/// <code>
/// public class OrderSummaryProjection : Projection&lt;OrderSummary&gt;,
///     IProject&lt;OrderSummary, OrderCreated&gt;,
///     IProject&lt;OrderSummary, OrderConfirmed&gt;
/// {
///     public string GetDocumentId(OrderCreated e) => e.OrderId.ToString();
///     public ProjectionResult&lt;OrderSummary&gt; Apply(OrderSummary state, OrderCreated e, ProjectionContext ctx)
///         => new OrderSummary { OrderId = e.OrderId, Status = "Created" };
///
///     public string GetDocumentId(OrderConfirmed e) => e.OrderId.ToString();
///     public ProjectionResult&lt;OrderSummary&gt; Apply(OrderSummary state, OrderConfirmed e, ProjectionContext ctx)
///         => state with { Status = "Confirmed" };
/// }
/// </code>
/// </example>
[Obsolete("Use DeclareProjection.For<TState>() instead. This type will be removed in a future version.")]
public abstract class Projection<TState> where TState : new()
{
    private readonly ProjectionDispatcher<TState> _dispatcher;

    protected Projection()
    {
        _dispatcher = ProjectionDispatcher<TState>.For(this);
    }

    /// <summary>
    /// The event types this projection handles.
    /// </summary>
    public IReadOnlySet<string> HandledEventTypes => _dispatcher.HandledEventTypes;

    /// <summary>
    /// Apply an event and get the result with change tracking intent.
    /// </summary>
    public ProjectionResult<TState> Apply(TState state, IEventEnvelope envelope)
        => _dispatcher.Apply(state, envelope);

    /// <summary>
    /// Get the document ID for an event. Different events may target different documents.
    /// </summary>
    public string GetDocumentId(IEventEnvelope envelope)
        => _dispatcher.GetDocumentId(envelope);

    /// <summary>
    /// Fold multiple events, returning final state.
    /// Useful for testing and rebuilding state from history.
    /// Note: This ignores Delete results (returns new TState) and Unchanged results (keeps current state).
    /// </summary>
    public TState Fold(TState initial, IEnumerable<IEventEnvelope> envelopes)
        => envelopes.Aggregate(initial, (state, env) => Apply(state, env) switch
        {
            ProjectionResult<TState>.Set s => s.State,
            ProjectionResult<TState>.Delete => new TState(),
            _ => state
        });
}
