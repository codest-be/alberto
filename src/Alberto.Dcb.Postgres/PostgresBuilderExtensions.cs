using Alberto.Dcb;
using Alberto.Dcb.Append;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Extension methods for configuring the PostgreSQL event store backend.
/// </summary>
public static class PostgresBuilderExtensions
{
    /// <summary>
    /// Uses PostgreSQL as this module's event store.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">
    /// Transforms the default options. Use a <c>with</c> expression:
    /// <c>o => o with { ConnectionString = cs, Schema = "orders" }</c>. Every property is also
    /// settable from <c>Alberto:Modules:{moduleKey}:Postgres</c>, which wins over this callback.
    /// </param>
    /// <remarks>
    /// This declares the backend. No connection is opened and no migration runs until the host
    /// starts, so building a service provider is always side-effect free.
    /// </remarks>
    public static DcbModuleBuilder WithPostgres(
        this DcbModuleBuilder builder,
        Func<PostgresOptions, PostgresOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = configure(new PostgresOptions())
            ?? throw new InvalidOperationException("WithPostgres configurator returned null.");

        return builder.UseBackend(new PostgresBackendDescriptor(options));
    }

    internal static void RegisterSingleTenantBackend(
        AlbertoModuleContext context, PostgresRuntimeOptions runtime)
    {
        var services = context.Services;
        var moduleKey = context.ModuleKey;

        services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var opts = runtime.Resolve(sp);
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var rawBackend = new PostgresEventStoreBackend(
                dataSource, timeProvider, opts.Schema, opts.EnableStableHeadBarrier);

            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(rawBackend, pipeline);
        });

        services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, key) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
            var eventStore = new EventStore(backend);
            RegisterInlineProjections(sp, key, eventStore);
            RegisterPostAppendHandlers(sp, key, eventStore);
            return eventStore;
        });
    }

    internal static void RegisterTenantBackend(
        AlbertoModuleContext context, PostgresRuntimeOptions runtime)
    {
        var services = context.Services;
        var moduleKey = context.ModuleKey;

        // Register tenancy services.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantAccessor, TenantAccessor>();

        // Register the tenant-aware backend (not as IEventStoreBackend — only used by decorator).
        services.AddKeyedSingleton<PostgresTenantEventStoreBackend>(moduleKey + ":tenant-raw", (sp, _) =>
        {
            var opts = runtime.Resolve(sp);
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new PostgresTenantEventStoreBackend(
                dataSource, timeProvider, opts.Schema, opts.EnableStableHeadBarrier);
        });

        // Register IEventStoreBackend (keyed, scoped) as decorator chain:
        // InterceptingBackend(TenantDecorator(TenantBackend))
        // Used by the API request path — scoped so ITenantAccessor is resolved per request.
        services.AddKeyedScoped<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var rawTenantBackend = sp.GetRequiredKeyedService<PostgresTenantEventStoreBackend>(moduleKey + ":tenant-raw");
            var tenantAccessor = sp.GetRequiredService<ITenantAccessor>();
            var decorator = new TenantEventStoreDecorator(rawTenantBackend, tenantAccessor);

            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(decorator, pipeline);
        });

        // Register a singleton backend for ControlLoops (streams all events, no per-request
        // tenant scoping). ControlLoops are singletons and cannot consume scoped services, so
        // they use this key.
        services.AddKeyedSingleton<IEventStoreBackend>(moduleKey + ":consumer", (sp, _) =>
        {
            var rawTenantBackend = sp.GetRequiredKeyedService<PostgresTenantEventStoreBackend>(moduleKey + ":tenant-raw");
            var decorator = new TenantEventStoreDecorator(rawTenantBackend, ConsumerTenantAccessor.Instance);
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(decorator, pipeline);
        });

        services.AddKeyedScoped<IEventStore>(moduleKey, (sp, key) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
            var eventStore = new EventStore(backend);
            RegisterInlineProjections(sp, key, eventStore);
            RegisterPostAppendHandlers(sp, key, eventStore);
            return eventStore;
        });
    }

    private static void RegisterPostAppendHandlers(IServiceProvider sp, object? key, IEventStoreConfigurator eventStore)
    {
        foreach (var handler in sp.GetKeyedServices<IPostAppendHandler>(key))
            eventStore.RegisterPostAppendHandler(handler);
    }

    private static void RegisterInlineProjections(IServiceProvider sp, object? key, IEventStoreConfigurator eventStore)
    {
        foreach (var projection in sp.GetKeyedServices<IInlineProjection>(key))
            eventStore.RegisterInlineProjection(projection);
    }
}

/// <summary>
/// No-op tenant accessor used for the ControlLoop singleton backend.
/// ControlLoops only call StreamAllAsync/GetPositionsAsync which do not use tenant context.
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
