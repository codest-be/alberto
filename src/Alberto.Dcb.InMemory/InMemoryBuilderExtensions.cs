using Alberto.Dcb.Append;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// Extension methods for configuring in-memory backend.
/// </summary>
public static class InMemoryBuilderExtensions
{
    /// <summary>
    /// Uses an in-process event store for this module. Nothing is durable; use it for tests,
    /// samples and local development.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseBackend(new InMemoryBackendDescriptor());
    }

    /// <summary>
    /// Uses the in-process event store belonging to <paramref name="sharedModuleKey"/>, so
    /// several modules observe one event log. Useful when a test spans two modules.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="sharedModuleKey">The module key whose event store backend to share.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder, string sharedModuleKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedModuleKey);

        return builder.UseBackend(new InMemoryBackendDescriptor { SharedModuleKey = sharedModuleKey });
    }

    /// <summary>
    /// Registers the in-memory backend's services. Called by
    /// <see cref="InMemoryBackendDescriptor.Register"/> once the declaration is final.
    /// </summary>
    internal static void RegisterBackend(AlbertoModuleContext context, string? sharedModuleKey)
    {
        var services = context.Services;
        var moduleKey = context.ModuleKey;

        // Create shared instances for stores (identical in single- and multi-tenant mode:
        // checkpoint and dead-letter state is not tenant-partitioned at the store level).
        var checkpointStore = new InMemoryCheckpointStore();
        var deadLetterStore = new InMemoryDeadLetterStore();

        // Register append interceptor pipeline
        services.AddKeyedSingleton<IAppendInterceptorPipeline>(moduleKey, (sp, _) =>
        {
            var interceptors = sp.GetKeyedServices<IAppendInterceptor>(moduleKey);
            return new AppendInterceptorPipeline(interceptors);
        });

        if (sharedModuleKey is not null)
        {
            // Resolve the shared backend from the other module.
            // Sharing a backend across modules is a test convenience and is always
            // single-tenant. Combining a shared backend with .WithTenancy() is rejected
            // by the validator (ALB0017) before this code is ever reached, so no tenancy
            // branching is needed here.
            services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
            {
                var sharedBackend = sp.GetRequiredKeyedService<IEventStoreBackend>(sharedModuleKey);
                var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
                return new InterceptingEventStoreBackend(sharedBackend, pipeline);
            });
        }
        else if (context.TenancyEnabled)
        {
            RegisterTenantBackend(services, moduleKey);
        }
        else
        {
            RegisterSingleTenantBackend(services, moduleKey);
        }

        // IEventStore is registered as singleton in the single-tenant path and as scoped in
        // the tenant path; this unconditional block runs for the shared-module path only.
        // The single-tenant and tenant paths register their own IEventStore inside their helpers.
        if (sharedModuleKey is not null)
        {
            services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, key) =>
            {
                var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
                return new EventStore(
                    backend,
                    sp.GetKeyedServices<IInlineProjection>(key),
                    sp.GetKeyedServices<IPostAppendHandler>(key));
            });
        }

        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, checkpointStore);
        services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, deadLetterStore);
    }

    private static void RegisterSingleTenantBackend(IServiceCollection services, string moduleKey)
    {
        // Singleton raw backend + singleton event store — no per-request scoping needed.
        services.AddKeyedSingleton<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var rawBackend = new InMemoryEventStoreBackend(timeProvider);
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(rawBackend, pipeline);
        });

        services.AddKeyedSingleton<IEventStore>(moduleKey, (sp, key) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
            return new EventStore(
                backend,
                sp.GetKeyedServices<IInlineProjection>(key),
                sp.GetKeyedServices<IPostAppendHandler>(key));
        });
    }

    private static void RegisterTenantBackend(IServiceCollection services, string moduleKey)
    {
        // Register tenancy services (idempotent — AddScoped does not throw if already registered
        // by another module in the same application).
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantAccessor, TenantAccessor>();

        // Singleton raw backend — shared between the request-scoped decorator and the consumer
        // singleton. The decorator itself is scoped so it can capture a per-request ITenantAccessor.
        services.AddKeyedSingleton<InMemoryEventStoreBackend>(moduleKey + ":tenant-raw", (sp, _) =>
        {
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new InMemoryEventStoreBackend(timeProvider);
        });

        // IEventStoreBackend (keyed, scoped) — decorator chain for the request path:
        //   InterceptingBackend → InMemoryTenantEventStoreDecorator → InMemoryEventStoreBackend
        services.AddKeyedScoped<IEventStoreBackend>(moduleKey, (sp, _) =>
        {
            var rawBackend = sp.GetRequiredKeyedService<InMemoryEventStoreBackend>(moduleKey + ":tenant-raw");
            var tenantAccessor = sp.GetRequiredService<ITenantAccessor>();
            var decorator = new InMemoryTenantEventStoreDecorator(rawBackend, tenantAccessor);
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(decorator, pipeline);
        });

        // ControlLoop consumer backend (singleton) — streams across all tenants without
        // per-request tenant scoping. Mirrors the ':consumer' key used by the Postgres backend.
        services.AddKeyedSingleton<IEventStoreBackend>(moduleKey + ":consumer", (sp, _) =>
        {
            var rawBackend = sp.GetRequiredKeyedService<InMemoryEventStoreBackend>(moduleKey + ":tenant-raw");
            var decorator = new InMemoryTenantEventStoreDecorator(rawBackend, ConsumerInMemoryTenantAccessor.Instance);
            var pipeline = sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey);
            return new InterceptingEventStoreBackend(decorator, pipeline);
        });

        // IEventStore is scoped because the backend it wraps is scoped.
        services.AddKeyedScoped<IEventStore>(moduleKey, (sp, key) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(key);
            return new EventStore(
                backend,
                sp.GetKeyedServices<IInlineProjection>(key),
                sp.GetKeyedServices<IPostAppendHandler>(key));
        });
    }
}

/// <summary>
/// No-op tenant accessor used for the ControlLoop singleton backend in multi-tenant
/// in-memory mode. Mirrors <c>ConsumerTenantAccessor</c> in the Postgres package.
/// ControlLoops only call <c>StreamAllAsync</c>/<c>GetPositionsAsync</c> which do not use
/// tenant context, so <c>HasTenant = false</c> is the correct sentinel value.
/// </summary>
file sealed class ConsumerInMemoryTenantAccessor : Alberto.Dcb.Tenancy.ITenantAccessor
{
    public static readonly ConsumerInMemoryTenantAccessor Instance = new();
    private ConsumerInMemoryTenantAccessor() { }

    public string TenantId =>
        throw new InvalidOperationException("Consumer backend does not support tenant-scoped operations.");

    public string? TenantIdOrDefault => null;

    public bool HasTenant => false;
}
