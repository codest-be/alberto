using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Filters;

/// <summary>
/// Filter that automatically sets the tenant context from the event's tenant ID
/// This ensures all subsequent filters and handlers execute within the correct tenant scope
/// </summary>
public sealed class TenantScopeFilter(ITenantContext tenantContext, ILogger<TenantScopeFilter> logger) : IConsumeFilter
{
    public async ValueTask Execute(
        object @event,
        EventContext context,
        Func<ValueTask> next,
        CancellationToken cancellationToken = default)
    {
        // Set the tenant context from the event's tenant ID
        var originalTenant = tenantContext.Tenant;
        var eventTenant = new Tenant(context.TenantId);

        logger.LogDebug(
            "Setting tenant context to {TenantId} for event {EventType} at position {Position}",
            eventTenant.Id,
            context.EventType,
            context.GlobalPosition
        );

        // Set tenant context from event
        tenantContext.Tenant = eventTenant;

        try
        {
            await next();
        }
        finally
        {
            tenantContext.Tenant = originalTenant;
        }
    }
}