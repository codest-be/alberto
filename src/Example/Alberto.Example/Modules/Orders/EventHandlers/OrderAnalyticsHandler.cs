using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.Example.Modules.Orders.EventHandlers;

/// <summary>
/// Analytics handler that tracks order metrics
/// </summary>
[Subscription("order-analytics")]
public class OrderAnalyticsHandler(ILogger<OrderAnalyticsHandler> logger) :
    IHandleEvent<OrderCreated>,
    IHandleEvent<OrderShipped>,
    IHandleEvent<OrderCancelled>
{
    public ValueTask Handle(OrderCreated @event, EventContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Analytics: Order {OrderId} created for {Amount:C} by customer {CustomerId} at position {Position}",
            @event.OrderId,
            @event.Amount,
            @event.CustomerId,
            context.GlobalPosition
        );

        // Example: Update analytics/metrics
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(OrderShipped @event, EventContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Analytics: Order {OrderId} shipped at position {Position}",
            @event.OrderId,
            context.GlobalPosition
        );

        // Example: Track fulfillment metrics
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(OrderCancelled @event, EventContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Analytics: Order {OrderId} cancelled at position {Position}",
            @event.OrderId,
            context.GlobalPosition
        );

        // Example: Track cancellation metrics
        return ValueTask.CompletedTask;
    }
}