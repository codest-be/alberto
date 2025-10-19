using Alberto.EventSourcing.Projections;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Batching;
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
    /// If called within a ProjectionBatchScope, accumulates updates in memory for batch commit.
    /// Otherwise, saves immediately to the repository.
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

        // Check if we're in a batch scope
        var batchScope = ProjectionBatchScope.Current;

        if (batchScope != null)
        {
            // Get or create accumulator for this handler instance (stored in the scope, not AsyncLocal)
            var accumulator = batchScope.GetOrCreateAccumulator(
                this, // Use handler instance as key
                () =>
                {
                    var newAccumulator = new ProjectionBatchAccumulator<TKey, TState>(repository, projector);

                    // Register commit action with the batch scope
                    batchScope.RegisterCommit(ct => newAccumulator.CommitAsync(ct));

                    return newAccumulator;
                });

            // Accumulate event with context
            accumulator.Add(key, @event, context);

            logger.LogDebug(
                "Accumulated event {EventType} at position {Position} for projection key {Key} in batch",
                context.EventType,
                context.GlobalPosition,
                key
            );
        }
        else
        {
            // No batch scope - save immediately (existing behavior)
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
}