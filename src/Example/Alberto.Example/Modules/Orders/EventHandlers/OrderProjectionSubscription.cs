using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.Example.Modules.Orders.EventHandlers;

// Example event types
[EventType("order-created")]
public record OrderCreated([property: Tag("order")] string OrderId, decimal Amount, string CustomerId);

[EventType("order-placed")]
public record OrderPlaced(string OrderId, decimal Amount, string CustomerId);

[EventType("order-shipped")]
public record OrderShipped(string OrderId, string TrackingNumber);

[EventType("order-cancelled")]
public record OrderCancelled(string OrderId, string Reason);

/// <summary>
/// Example event handler that processes order-related events across all tenants
/// </summary>
[Subscription("order-projection")]
public class OrderProjectionSubscription(ITenantContext tenantContext, ILogger<OrderProjectionSubscription> logger) :
    IHandleEvent<OrderCreated>,
    IHandleEvent<OrderPlaced>,
    IHandleEvent<OrderShipped>,
    IHandleEvent<OrderCancelled>
{
    public ValueTask Handle(OrderCancelled @event, EventContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Order cancelled: {OrderId} - {Reason} in tenant {TenantId} at position {Position}",
            @event.OrderId,
            @event.Reason,
            tenantContext.Tenant.Id,
            context.GlobalPosition
        );

        // Example: Process refund, update inventory
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(OrderCreated @event, EventContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Order created: {OrderId} for {Amount:C} by customer {CustomerId} in tenant {TenantId} at position {Position}",
            @event.OrderId,
            @event.Amount,
            @event.CustomerId,
            context.TenantId,
            context.GlobalPosition
        );

        // Example: Create initial order projections/read models
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(OrderPlaced @event, EventContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Order placed: {OrderId} for {Amount:C} by customer {CustomerId} in tenant {TenantId} at position {Position}",
            @event.OrderId,
            @event.Amount,
            @event.CustomerId,
            context.TenantId,
            context.GlobalPosition
        );

        // Example: Update order projections/read models
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(OrderShipped @event, EventContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Order shipped: {OrderId} with tracking {TrackingNumber} in tenant {TenantId} at position {Position}",
            @event.OrderId,
            @event.TrackingNumber,
            context.TenantId,
            context.GlobalPosition
        );

        // Example: Send notification, update shipping status
        return ValueTask.CompletedTask;
    }
}