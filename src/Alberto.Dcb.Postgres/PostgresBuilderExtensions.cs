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

        // P1.4 (TEN-6): Validate that the schema was created with a tenancy mode consistent
        // with the application configuration. Runs even when AutoMigrate is false so a
        // wrong-mode migration applied manually is caught before any damage is done.
        // When AutoMigrate is false the DB is expected to already exist and be fully migrated;
        // a connection failure here means the DB is unavailable at startup (appropriate to fail).
        PostgresMigrator.ValidateTenancyMode(
            options.ConnectionString,
            options.Schema,
            singleTenant: !isTenantMode);

        // DX-6: Detect .WithTenancy() called AFTER .WithPostgres(). When the builder's HasTenancy
        // flag is read here it reflects the state at registration time. The startup validator
        // below re-reads it after the fluent chain is complete and fails fast if the flags differ,
        // meaning WithTenancy() was chained too late for the backend wiring to pick it up.
        // The hosted service runs before the application starts serving requests.
        builder.Services.AddSingleton<IHostedService>(
            _ => new TenancyOrderingValidator(builder, isTenantMode));

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
/// Startup validator that fails fast when <c>.WithTenancy()</c> was called after
/// <c>.WithPostgres()</c> on the same builder — a mis-ordering that causes the backend to be
/// wired in single-tenant mode even though the application intends multi-tenant operation.
/// </summary>
/// <remarks>
/// The validator captures the tenancy mode seen by <see cref="PostgresBuilderExtensions.WithPostgres"/>
/// at registration time (<paramref name="tenancyModeAtRegistration"/>), then re-reads
/// <see cref="DcbModuleBuilder.HasTenancy"/> at <see cref="StartAsync"/> time (after the
/// fluent chain is complete). A difference means the ordering was wrong.
/// </remarks>
file sealed class TenancyOrderingValidator(
    DcbModuleBuilder builder,
    bool tenancyModeAtRegistration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var currentTenancyMode = builder.HasTenancy;
        if (currentTenancyMode != tenancyModeAtRegistration)
            throw new InvalidOperationException(
                $"Alberto configuration error for module '{builder.ModuleKey}': " +
                ".WithTenancy() was called AFTER .WithPostgres(), so the backend was wired " +
                "in single-tenant mode and will silently ignore the tenancy flag. " +
                "Fix: call .WithTenancy() before .WithPostgres() in your configuration chain.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
