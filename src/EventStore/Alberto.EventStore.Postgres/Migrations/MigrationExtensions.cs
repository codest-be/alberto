using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Extension methods for adding unified database migrations with auto-discovery.
/// </summary>
public static class MigrationExtensions
{
    /// <summary>
    /// Adds a hosted service that runs all discovered migrations on startup.
    /// Connection strings are automatically discovered from registered PostgresEventStore configurations.
    /// Discovers and executes all IEventStoreMigration implementations found in loaded assemblies.
    /// Each schema tracks its own migrations independently in a {schema}.__migrations table.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDatabaseMigrations(
        this IServiceCollection services)
    {
        // Register the unified migration hosted service with auto-discovery
        services.AddSingleton<IHostedService>(sp =>
            new MigrationHostedService(
                sp,
                sp.GetRequiredService<ILogger<MigrationHostedService>>()));

        return services;
    }
}