using Alberto.Dcb.Admin.Internal;
using Alberto.Dcb.Append;
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
            var migrationResult = PostgresMigrator.Migrate(options.ConnectionString, options.Schema);
            if (!migrationResult.Successful)
            {
                throw new InvalidOperationException(
                    $"Database migration failed: {migrationResult.Error?.Message}",
                    migrationResult.Error);
            }
        }

        var moduleKey = builder.ModuleKey;
        var schema = options.Schema;

        // Register NpgsqlDataSource with connection pool settings
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
        dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = options.MaxPoolSize;
        dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = options.MinPoolSize;

        builder.Services.AddKeyedSingleton(moduleKey, dataSourceBuilder.Build());

        // Register append interceptor pipeline
        builder.Services.AddKeyedSingleton<IAppendInterceptorPipeline>(moduleKey, (sp, _) =>
        {
            var interceptors = sp.GetKeyedServices<IAppendInterceptor>(moduleKey);
            return new AppendInterceptorPipeline(interceptors);
        });

        // Register event store backend with intercepting decorator
        builder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var rawBackend = new PostgresEventStoreBackend(dataSource, timeProvider, schema);

            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(rawBackend, pipeline);
        });

        // Register event store (uses intercepting backend)
        builder.Services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, _) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            return new PostgresEventStore(backend);
        });

        // Register checkpoint store with caching layer
        builder.Services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var postgresStore = new PostgresCheckpointStore(dataSource, schema);
            return new CachingCheckpointStore(postgresStore);
        });

        // Register dead letter store
        builder.Services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresDeadLetterStore(dataSource, schema);
        });

        // Register admin data access
        builder.Services.AddKeyedSingleton<IAdminDataAccess>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresAdminDataAccess(dataSource, schema);
        });

        return builder;
    }
}
