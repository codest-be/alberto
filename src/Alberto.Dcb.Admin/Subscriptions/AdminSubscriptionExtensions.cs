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
        services.AddSingleton<InMemoryProcessorStatusPublisher>();
        services.AddSingleton<IProcessorStatusPublisher>(sp =>
            sp.GetRequiredService<InMemoryProcessorStatusPublisher>());
        services.AddHostedService<ProcessorStatusMonitor>();

        return services;
    }
}
