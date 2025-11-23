using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Projections;

namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Typed projection subscription interface with strongly-typed key and state.
/// Provides access to projector and repository for batch processing by EventRouter.
/// </summary>
/// <typeparam name="TKey">The projection key type</typeparam>
/// <typeparam name="TState">The projection state type</typeparam>
/// <example>
/// <code>
/// public class OrderProjectionSubscription : IProjectionSubscription&lt;Guid, Order&gt;,
///     IHandleEvent&lt;OrderCreated&gt;,
///     IHandleEvent&lt;OrderPlaced&gt;
/// {
///     public IProjector&lt;Order&gt; Projector { get; }
///     public IProjectionRepository&lt;Guid, Order&gt; Repository { get; }
///
///     public OrderProjectionSubscription(
///         IProjectionRepository&lt;Guid, Order&gt; repository,
///         OrderProjector projector)
///     {
///         Repository = repository;
///         Projector = projector;
///     }
///
///     public Guid GetKey(object @event) =&gt; @event switch
///     {
///         OrderCreated e =&gt; e.OrderId,
///         OrderPlaced e =&gt; e.OrderId,
///         _ =&gt; throw new InvalidOperationException()
///     };
///
///     // Handle methods can be empty - EventRouter routes via IProjectionSubscription
///     public ValueTask Handle(OrderCreated e, EventContext ctx, CancellationToken ct)
///         =&gt; ValueTask.CompletedTask;
///     public ValueTask Handle(OrderPlaced e, EventContext ctx, CancellationToken ct)
///         =&gt; ValueTask.CompletedTask;
/// }
/// </code>
/// </example>
public interface IProjectionSubscription<TKey, TState> : IProjectionSubscription
    where TKey : notnull
    where TState : new()
{
    /// <summary>
    /// Get the projector for this subscription.
    /// Used by EventRouter for batch event processing.
    /// </summary>
    IProjector<TState> Projector { get; }

    /// <summary>
    /// Get the repository for this subscription.
    /// Used by EventRouter for batch loading and saving.
    /// </summary>
    IProjectionRepository<TKey, TState> Repository { get; }

    IEnumerable<object> IProjectionSubscription.GetKeys(object @event)
        => new object[] { GetKey(@event) };

    /// <summary>
    /// Extract the projection key from an event
    /// </summary>
    TKey GetKey(object @event);
}