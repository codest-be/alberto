using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.Example.Modules.Orders.Filters;

/// <summary>
/// Filter that logs all events being processed
/// </summary>
public class LoggingFilter(ILogger<LoggingFilter> logger) : IConsumeFilter
{
    public async ValueTask Execute(
        object @event,
        EventContext context,
        Func<ValueTask> next,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Processing event {EventType} at position {Position} for tenant {TenantId}",
            context.EventType,
            context.GlobalPosition,
            context.TenantId
        );

        await next();

        logger.LogDebug(
            "Completed processing event {EventType} at position {Position}",
            context.EventType,
            context.GlobalPosition
        );
    }
}