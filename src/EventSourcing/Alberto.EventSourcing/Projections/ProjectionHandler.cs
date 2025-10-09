using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Helper class that encapsulates the logic for handling events in a projection repository.
/// Reduces boilerplate in subscription handlers by centralizing the projection logic.
///
/// Usage:
/// <code>
/// [Subscription("order-projection")]
/// public class OrderProjectionSubscription(ProjectionHandler&lt;Guid, OrderState&gt; handler) :
///     IHandleEvent&lt;OrderCreated&gt;,
///     IHandleEvent&lt;OrderPlaced&gt;
/// {
///     public ValueTask Handle(OrderCreated e, EventContext ctx, CancellationToken ct)
///         =&gt; handler.Handle(e.OrderId, e, ctx, ct);
///
///     public ValueTask Handle(OrderPlaced e, EventContext ctx, CancellationToken ct)
///         =&gt; handler.Handle(e.OrderId, e, ctx, ct);
/// }
/// </code>
/// </summary>
/// <typeparam name="TKey">The type of the projection key</typeparam>
/// <typeparam name="TState">The projected state type</typeparam>
public sealed class ProjectionHandler<TKey, TState>(
    IProjectionRepository<TKey, TState> repository,
    IProjector<TState> projector,
    ILogger<ProjectionHandler<TKey, TState>> logger)
    where TKey : notnull
    where TState : new()
{
    /// <summary>
    /// Handles an event by projecting it and storing the result.
    /// </summary>
    /// <param name="key">The projection key extracted from the event</param>
    /// <param name="event">The event to project</param>
    /// <param name="context">Event context with metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask Handle(
        TKey key,
        object @event,
        EventContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Processing event {EventType} at position {Position} for projection key {Key}",
            context.EventType,
            context.GlobalPosition,
            key
        );

        // Load current state (or create new if not exists)
        var currentState = await repository.Get(key, cancellationToken) ?? new TState();

        // Apply the event to project new state
        var newState = projector.Apply(currentState, @event);

        // Store the updated projection
        await repository.Upsert(key, newState, cancellationToken);

        logger.LogDebug(
            "Updated projection {Key} from event {EventType} at position {Position}",
            key,
            context.EventType,
            context.GlobalPosition
        );
    }
}