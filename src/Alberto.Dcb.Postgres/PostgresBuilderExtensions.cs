using Alberto.Dcb.Admin.Internal;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Extension methods for configuring PostgreSQL backend.
/// </summary>
public static class PostgresBuilderExtensions
{
    /// <summary>
    /// Configures the module to use PostgreSQL for event storage.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Action to configure PostgreSQL options.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithPostgres(
        this DcbModuleBuilder builder,
        Action<PostgresOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PostgresOptions();
        configure(options);

        if (string.IsNullOrEmpty(options.ConnectionString))
            throw new InvalidOperationException("PostgreSQL connection string is required.");

        // Run migrations if enabled
        if (options.AutoMigrate)
        {
            var migrationResult = PostgresMigrator.Migrate(options.ConnectionString);
            if (!migrationResult.Successful)
            {
                throw new InvalidOperationException(
                    $"Database migration failed: {migrationResult.Error?.Message}",
                    migrationResult.Error);
            }
        }

        var moduleKey = builder.ModuleKey;

        // Register NpgsqlDataSource
        builder.Services.AddKeyedSingleton(moduleKey,
            NpgsqlDataSource.Create(options.ConnectionString));

        // Register event store backend
        builder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new PostgresEventStoreBackend(dataSource, timeProvider);
        });

        // Register event store
        builder.Services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new PostgresEventStore(dataSource, timeProvider);
        });

        // Register checkpoint store
        builder.Services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresCheckpointStore(dataSource);
        });

        // Register dead letter store
        builder.Services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresDeadLetterStore(dataSource);
        });

        // Register admin data access
        builder.Services.AddKeyedSingleton<IAdminDataAccess>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresAdminDataAccess(dataSource);
        });

        return builder;
    }
}
