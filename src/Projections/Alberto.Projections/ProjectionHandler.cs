using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Alberto.Projections;

/// <summary>
/// Helper class that encapsulates the logic for handling events in a projection repository.
/// Reduces boilerplate in subscription handlers by centralizing the projection logic.
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
    /// Uses version-aware updates to ensure idempotency based on EventContext.GlobalPosition.
    /// </summary>
    /// <param name="key">The projection key extracted from the event</param>
    /// <param name="event">The event to project</param>
    /// <param name="context">Event context with metadata including GlobalPosition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask Handle(
        TKey key,
        object @event,
        EventContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Processing event {EventType} at global position {Position} for projection key {Key}",
            context.EventType,
            context.GlobalPosition,
            key
        );

        // Use version-aware update to ensure idempotency
        var updated = await repository.UpdateWithVersion(
            key,
            state => projector.Apply(state, @event),
            context.GlobalPosition,
            cancellationToken);

        if (updated)
        {
            logger.LogDebug(
                "Updated projection {Key} from event {EventType} at position {Position}",
                key,
                context.EventType,
                context.GlobalPosition
            );
        }
        else
        {
            logger.LogDebug(
                "Skipped projection update for {Key} - event at position {Position} already processed or out of order",
                key,
                context.GlobalPosition
            );
        }
    }
}