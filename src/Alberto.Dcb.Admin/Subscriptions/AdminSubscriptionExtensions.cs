using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Extension methods for registering admin subscription services.
/// </summary>
public static class AdminSubscriptionExtensions
{
    /// <summary>
    /// Adds admin real-time subscription services.
    /// </summary>
    public static IServiceCollection AddAdminSubscriptions(this IServiceCollection services)
    {
        // Register unified publisher
        services.AddSingleton<InMemoryAdminPublisher>();
        services.AddSingleton<IAdminPublisher>(sp => sp.GetRequiredService<InMemoryAdminPublisher>());

        // Legacy support - redirect to unified publisher
        services.AddSingleton<InMemoryProcessorStatusPublisher>();
        services.AddSingleton<IProcessorStatusPublisher>(sp => sp.GetRequiredService<InMemoryProcessorStatusPublisher>());

        // Register monitor
        services.AddHostedService<AdminMonitor>();

        return services;
    }
}
