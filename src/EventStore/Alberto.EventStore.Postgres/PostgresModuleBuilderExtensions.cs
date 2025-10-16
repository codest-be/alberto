using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres.Subscriptions.Checkpoints;
using Alberto.EventStore.Postgres.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Polling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.EventStore.Postgres;

/// <summary>
/// Extension methods for configuring PostgreSQL backend in ModuleBuilder
/// </summary>
public static class PostgresModuleBuilderExtensions
{
    /// <summary>
    /// Configures this EventStore module to use PostgreSQL as the storage backend
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore type</typeparam>
    /// <param name="moduleBuilder">The module builder</param>
    /// <param name="configureOptions">Configuration action for PostgreSQL options</param>
    /// <returns>The module builder for chaining</returns>
    public static ModuleBuilder<TEventStore> WithPostgres<TEventStore>(
        this ModuleBuilder<TEventStore> moduleBuilder,
        Action<PostgresEventStoreOptions> configureOptions)
        where TEventStore : EventStoreFactory
    {
        var options = new PostgresEventStoreOptions();
        configureOptions(options);

        // Register options with the module key
        moduleBuilder.Services.Configure(moduleBuilder.ModuleKey, configureOptions);

        // Register PostgreSQL backend implementation with the module key
        moduleBuilder.Services.AddKeyedScoped<IEventStoreBackend, PostgresEventStoreBackend>(
            moduleBuilder.ModuleKey,
            (provider, _) => new PostgresEventStoreBackend(
                Options.Create(
                    provider.GetRequiredService<IOptionsSnapshot<PostgresEventStoreOptions>>()
                        .Get(moduleBuilder.ModuleKey)),
                provider.GetRequiredService<ILogger<PostgresEventStoreBackend>>(),
                provider.GetRequiredService<IMetricsRecorder>()));

        // Register as IMultiTenantEventStore for subscription polling
        moduleBuilder.Services.AddKeyedScoped<IMultiTenantEventStore, PostgresEventStoreBackend>(
            moduleBuilder.ModuleKey,
            (provider, _) => new PostgresEventStoreBackend(
                Options.Create(
                    provider.GetRequiredService<IOptionsSnapshot<PostgresEventStoreOptions>>()
                        .Get(moduleBuilder.ModuleKey)),
                provider.GetRequiredService<ILogger<PostgresEventStoreBackend>>(),
                provider.GetRequiredService<IMetricsRecorder>()));

        // Register subscription infrastructure (checkpoint and poison pill stores)
        RegisterPostgresSubscriptionInfrastructure(moduleBuilder);

        // Register the EventStore factory
        moduleBuilder.RegisterEventStore();

        // Mark backend as configured
        moduleBuilder.MarkBackendConfigured();

        return moduleBuilder;
    }

    private static void RegisterPostgresSubscriptionInfrastructure<TEventStore>(
        ModuleBuilder<TEventStore> moduleBuilder)
        where TEventStore : EventStoreFactory
    {
        var services = moduleBuilder.Services;
        var moduleKey = moduleBuilder.ModuleKey;

        // Register checkpoint store with throttling
        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>().Get(moduleKey);
            var innerLogger = sp.GetRequiredService<ILogger<PostgresCheckpointStore>>();
            var throttledLogger = sp.GetRequiredService<ILogger<ThrottledCheckpointStore>>();

            // Create inner PostgreSQL checkpoint store
            var innerStore = new PostgresCheckpointStore(options.ConnectionString, options.Schema, innerLogger);

            // Try to get polling options (may not be configured if no subscriptions are set up)
            var pollingOptions = sp.GetKeyedService<PollingOptions>(moduleKey);
            var flushInterval = pollingOptions?.CheckpointFlushIntervalSeconds ?? 5;

            // Wrap with throttling to batch database writes
            return new ThrottledCheckpointStore(
                innerStore,
                TimeSpan.FromSeconds(flushInterval),
                throttledLogger);
        });

        // Register poison pill store
        services.AddKeyedSingleton<IPoisonPillStore>(moduleKey, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>().Get(moduleKey);
            var logger = sp.GetRequiredService<ILogger<PostgresPoisonPillStore>>();
            return new PostgresPoisonPillStore(options.ConnectionString, options.Schema, logger);
        });

        // Register default no-op trace context provider (can be overridden by WithTelemetry)
        services.TryAddSingleton<ITraceContextProvider, NoopTraceContextProvider>();

        // Register default filters that are always included
        RegisterDefaultFilters(services, moduleKey);
    }

    private static void RegisterDefaultFilters(IServiceCollection services, string moduleKey)
    {
        // TenantScopeFilter is always added first to ensure tenant context is set
        services.AddKeyedScoped<TenantScopeFilter>(moduleKey, (sp, _) =>
        {
            var tenantContext = sp.GetRequiredService<ITenantContext>();
            var logger = sp.GetRequiredService<ILogger<TenantScopeFilter>>();
            return new TenantScopeFilter(tenantContext, logger);
        });

        // Register TelemetryConsumeFilter for channel subscriptions (synchronous)
        services.AddKeyedScoped<TelemetryConsumeFilter>($"{moduleKey}:channel", (sp, _) =>
        {
            var traceContextProvider = sp.GetRequiredService<ITraceContextProvider>();
            return new TelemetryConsumeFilter(traceContextProvider, isSynchronous: true);
        });

        // Register TelemetryConsumeFilter for polling subscriptions (asynchronous)
        services.AddKeyedScoped<TelemetryConsumeFilter>($"{moduleKey}:polling", (sp, _) =>
        {
            var traceContextProvider = sp.GetRequiredService<ITraceContextProvider>();
            return new TelemetryConsumeFilter(traceContextProvider, isSynchronous: false);
        });
    }
}