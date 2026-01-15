using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Extension methods for registering tenancy services.
/// </summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Adds tenancy services to the service collection.
    /// This registers <see cref="TenantContext"/> and <see cref="ITenantAccessor"/> as scoped services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        return services;
    }
}
