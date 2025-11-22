using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Extension methods for adding EventStore schema migrations with pluggable migration strategies.
/// </summary>
public static class MigrationExtensions
{
    /// <summary>
    /// Adds a hosted service that runs EventStore schema migrations on startup using configured IMigrationStrategy.
    /// Connection strings and schemas are automatically discovered from registered PostgresEventStore configurations.
    /// Each schema uses its configured MigrationStrategy (NoMigration, ScriptOnly, or AutoMigration).
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDatabaseMigrations(
        this IServiceCollection services)
    {
        services.AddSingleton<IHostedService>(sp =>
            new MigrationHostedService(
                sp.GetRequiredService<PostgresSchemaRegistry>(),
                sp.GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>(),
                sp.GetRequiredService<ILogger<MigrationHostedService>>()));

        return services;
    }
}