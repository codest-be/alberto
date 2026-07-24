using Alberto.Dcb.Append;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Extension methods for configuring PostgreSQL backend.
/// </summary>
public static class PostgresBuilderExtensions
{
    /// <summary>
    /// Configures the module to use PostgreSQL for event storage.
    /// By default, single-tenant mode is used. Call .WithTenancy() before .WithPostgres()
    /// to enable multi-tenant mode.
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

        var isTenantMode = builder.HasTenancy;

        // Run migrations if enabled
        if (options.AutoMigrate)
        {
            var migrationResult = PostgresMigrator.Migrate(
                options.ConnectionString,
                options.Schema,
                singleTenant: !isTenantMode);

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

        var enableStableHeadBarrier = options.EnableStableHeadBarrier;

        if (isTenantMode)
        {
            // Multi-tenant mode: register PostgresTenantEventStoreBackend + TenantEventStoreDecorator
            // IEventStoreBackend and IEventStore are both scoped (backend captures ITenantAccessor per request)
            RegisterTenantBackend(builder, moduleKey, schema, enableStableHeadBarrier);
            builder.Services.AddKeyedScoped<IEventStore>(moduleKey, (sp, key) =>
            {
                var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
                var eventStore = new PostgresEventStore(backend);
                RegisterInlineProjections(sp, key, eventStore);
                RegisterPostAppendHandlers(sp, key, eventStore);
                return eventStore;
            });
        }
        else
        {
            // Single-tenant mode: register PostgresEventStoreBackend directly (singleton — no per-request state)
            RegisterSingleTenantBackend(builder, moduleKey, schema, enableStableHeadBarrier);
            builder.Services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, key) =>
            {
                var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
                var eventStore = new PostgresEventStore(backend);
                RegisterInlineProjections(sp, key, eventStore);
                RegisterPostAppendHandlers(sp, key, eventStore);
                return eventStore;
            });
        }

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

        // Register processor locks (consumer chooses which mode via WithSingleLeaderLock/WithTenantDistribution)
        builder.Services.AddKeyedSingleton<IProcessorLock>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresProcessorLock(dataSource);
        });

        var leaseDuration = options.LeaseDuration;
        builder.Services.AddKeyedSingleton<ITenantProcessorLock>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresTenantProcessorLock(dataSource, schema, leaseDuration);
        });

        builder.Services.AddKeyedSingleton<IProcessorLeaseManager>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresProcessorLeaseManager(dataSource, schema, leaseDuration);
        });

        // Append signal shared by the LISTEN/NOTIFY listener and EventStoreHead so
        // the head wakes immediately on append instead of waiting for its interval.
        builder.Services.AddKeyedSingleton<IEventAppendedSignal>(moduleKey, (_, _) => new EventAppendedSignal());

        if (options.EnableNotifyListener)
        {
            builder.Services.AddSingleton<IHostedService>(sp =>
            {
                var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
                var signal = sp.GetRequiredKeyedService<IEventAppendedSignal>(moduleKey);
                return new PostgresEventListener(
                    dataSource, schema, signal, sp.GetService<ILogger<PostgresEventListener>>());
            });
        }

        return builder;
    }

    private static void RegisterSingleTenantBackend(
        DcbModuleBuilder builder, string moduleKey, string? schema, bool enableStableHeadBarrier)
    {
        builder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var rawBackend = new PostgresEventStoreBackend(dataSource, timeProvider, schema, enableStableHeadBarrier);

            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(rawBackend, pipeline);
        });
    }

    private static void RegisterPostAppendHandlers(IServiceProvider sp, object? key, PostgresEventStore eventStore)
    {
        foreach (var handler in sp.GetKeyedServices<IPostAppendHandler>(key))
            eventStore.RegisterPostAppendHandler(handler);
    }

    private static void RegisterInlineProjections(IServiceProvider sp, object? key, PostgresEventStore eventStore)
    {
        foreach (var projection in sp.GetKeyedServices<IInlineProjection>(key))
            eventStore.RegisterInlineProjection(projection);
    }

    private static void RegisterTenantBackend(
        DcbModuleBuilder builder, string moduleKey, string? schema, bool enableStableHeadBarrier)
    {
        // Register tenancy services
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantAccessor, TenantAccessor>();

        // Register the tenant-aware backend (not as IEventStoreBackend — only used by decorator)
        builder.Services.AddKeyedSingleton<PostgresTenantEventStoreBackend>(moduleKey + ":tenant-raw", (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new PostgresTenantEventStoreBackend(dataSource, timeProvider, schema, enableStableHeadBarrier);
        });

        // Register IEventStoreBackend (keyed, scoped) as decorator chain: InterceptingBackend(TenantDecorator(TenantBackend))
        // Used by the API request path — scoped so ITenantAccessor is resolved per request.
        builder.Services.AddKeyedScoped<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var rawTenantBackend = sp.GetRequiredKeyedService<PostgresTenantEventStoreBackend>(moduleKey + ":tenant-raw");
            var tenantAccessor = sp.GetRequiredService<ITenantAccessor>();
            var decorator = new TenantEventStoreDecorator(rawTenantBackend, tenantAccessor);

            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(decorator, pipeline);
        });

        // Register a singleton backend for ControlLoops (streams all events, no per-request tenant scoping).
        // ControlLoops are singletons and cannot consume scoped services, so they use this key.
        // ControlLoops only call StreamAll and GetPositionsAsync — the null accessor is never exercised.
        builder.Services.AddKeyedSingleton<IEventStoreBackend>(moduleKey + ":consumer", (sp, _) =>
        {
            var rawTenantBackend = sp.GetRequiredKeyedService<PostgresTenantEventStoreBackend>(moduleKey + ":tenant-raw");
            var decorator = new TenantEventStoreDecorator(rawTenantBackend, ConsumerTenantAccessor.Instance);
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(decorator, pipeline);
        });
    }
}

/// <summary>
/// No-op tenant accessor used for the ControlLoop singleton backend.
/// ControlLoops only call StreamAll/GetPositionsAsync which do not use tenant context.
/// </summary>
file sealed class ConsumerTenantAccessor : Alberto.Dcb.Tenancy.ITenantAccessor
{
    public static readonly ConsumerTenantAccessor Instance = new();
    private ConsumerTenantAccessor() { }

    public string TenantId =>
        throw new InvalidOperationException("Consumer backend does not support tenant-scoped operations.");

    public string? TenantIdOrDefault => null;

    public bool HasTenant => false;
}
