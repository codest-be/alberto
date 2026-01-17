using Alberto.Dcb.Admin.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Postgres.Admin;

/// <summary>
/// Extension methods for registering PostgreSQL-based admin subscriptions.
/// </summary>
public static class PostgresAdminListenerExtensions
{
    /// <summary>
    /// Adds PostgreSQL LISTEN/NOTIFY based admin subscriptions.
    /// This replaces the polling-based AdminMonitor with push-based notifications.
    /// </summary>
    /// <remarks>
    /// Requires:
    /// - Migration 006_AdminNotifications.sql to be applied (adds NOTIFY triggers)
    /// - HotChocolate's AddInMemorySubscriptions() to be called first
    /// </remarks>
    public static IServiceCollection AddPostgresAdminSubscriptions(this IServiceCollection services)
    {
        // Register HotChocolate-based admin publisher
        services.AddSingleton<IAdminPublisher, HotChocolateAdminPublisher>();

        // Legacy support - redirect to unified publisher
        services.AddSingleton<InMemoryProcessorStatusPublisher>();
        services.AddSingleton<IProcessorStatusPublisher>(sp => sp.GetRequiredService<InMemoryProcessorStatusPublisher>());

        // Register listener instead of polling monitor
        services.AddHostedService<PostgresAdminListener>();

        return services;
    }
}
