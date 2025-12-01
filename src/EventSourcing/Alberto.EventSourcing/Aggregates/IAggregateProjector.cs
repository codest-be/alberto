using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;

namespace Alberto.EventSourcing.Aggregates;

/// <summary>
/// Marker interface for aggregate projectors that handle multiple event types
/// across multiple commands. Use this for "real aggregates" where multiple
/// commands operate on the same aggregate state.
/// </summary>
/// <typeparam name="TState">The aggregate's state</typeparam>
/// <example>
/// <code>
/// public sealed record OrderState
/// {
///     public bool Exists { get; init; }
///     public OrderStatus Status { get; init; } = OrderStatus.Draft;
///     public decimal Amount { get; init; }
///     public string CustomerId { get; init; } = string.Empty;
/// }
///
/// public sealed class OrderProjector : IAggregateProjector&lt;OrderState&gt;
/// {
///     public OrderState Apply(OrderState state, object @event)
///     {
///         return @event switch
///         {
///             OrderCreated e => state with { Exists = true, Status = OrderStatus.Created, Amount = e.Amount, CustomerId = e.CustomerId },
///             OrderPlaced => state with { Status = OrderStatus.Placed },
///             _ => state
///         };
///     }
///
///     public StreamQuery GetQuery(string aggregateId)
///     {
///         return new StreamQuery([new EventTag(Tags.Order, aggregateId)])
///             .WithEventType&lt;OrderCreated&gt;()
///             .WithEventType&lt;OrderPlaced&gt;();
///     }
/// }
/// </code>
/// </example>
public interface IAggregateProjector<TState> : IProjector<TState> where TState : new()
{
    /// <summary>
    /// Constructs a StreamQuery for loading this aggregate.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier (e.g., orderId)</param>
    /// <returns>StreamQuery configured for this aggregate's events</returns>
    StreamQuery GetQuery(string aggregateId);
}