using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Tenancy;

/// <summary>
/// Creates the per-event dependency scope and carries tenant identity from the event envelope
/// into scoped dependencies.
/// </summary>
public static class EventProcessingScope
{
    /// <summary>
    /// Creates an asynchronous scope and, when tenancy is configured, initializes its
    /// <see cref="TenantContext"/> from <paramref name="tenantId"/>.
    /// </summary>
    public static AsyncServiceScope Create(
        IServiceScopeFactory scopeFactory,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var scope = scopeFactory.CreateAsyncScope();
        if (!string.IsNullOrWhiteSpace(tenantId))
            scope.ServiceProvider.GetService<TenantContext>()?.SetTenant(tenantId);

        return scope;
    }
}
