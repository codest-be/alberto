#pragma warning disable ALB9001 // Whole file is the DI registration layer for the experimental sharding feature.
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// Registers what a sharded module needs on top of its per-shard registrations: the catalog,
/// the resolver that reads it, and the routers that sit under the logical module key.
/// </summary>
internal static class ShardingRegistration
{
    internal static void Register(IServiceCollection services, AlbertoModuleDefinition declared)
    {
        var moduleKey = declared.ModuleKey;

        // The tenant accessor is what routing reads the current tenant from. Each shard's
        // backend registration adds it too; TryAdd keeps one registration rather than one per
        // shard, since it is per-request state and not per-database.
        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantAccessor, TenantAccessor>();

        services.AddKeyedSingleton(moduleKey, (IServiceProvider _, object? _) => new ShardHealth());

        // Adds the check to the host's health-check pipeline if it has one. Configuring the
        // options is inert on its own — an app that never calls AddHealthChecks() has no
        // HealthCheckService to read them, so this costs nothing and needs no opt-in.
        services.Configure<HealthCheckServiceOptions>(options => options.Registrations.Add(
            new HealthCheckRegistration(
                $"alberto-shards-{moduleKey}",
                sp => new ShardHealthCheck(
                    moduleKey,
                    sp.GetRequiredKeyedService<ShardHealth>(moduleKey),
                    sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
                        .Get(moduleKey).Tenancy.ShardIds.ToArray()),
                failureStatus: HealthStatus.Unhealthy,
                tags: ["alberto", "shards"])));

        // The catalog lives in its own database, so it registers its own data source and
        // migration under a key of its own rather than borrowing a shard's.
        declared.Tenancy.Catalog?.RegisterShardCatalog(
            new AlbertoModuleContext(services, declared));

        services.AddKeyedSingleton(moduleKey, (IServiceProvider sp, object? key) =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
                .Get((string)key!);

            return new TenantShardResolver(
                moduleKey,
                sp.GetRequiredKeyedService<ITenantShardMap>(moduleKey),
                definition.Tenancy.ShardIds.ToHashSet(StringComparer.Ordinal),
                definition.Tenancy.DefaultShardId,
                sp.GetService<ILogger<TenantShardResolver>>());
        });

        services.AddSingleton<IHostedService>(sp =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
            return new TenantShardCacheRefresher(
                moduleKey,
                sp.GetRequiredKeyedService<TenantShardResolver>(moduleKey),
                definition.Tenancy.CatalogRefreshInterval,
                sp.GetService<ILogger<TenantShardCacheRefresher>>());
        });

        // The logical key resolves to a router rather than to any one database. Scoped, because
        // it reads the request's tenant.
        services.AddKeyedScoped<IEventStore>(moduleKey, (sp, _) => new ShardRoutingEventStore(
            moduleKey,
            sp.GetRequiredService<ITenantAccessor>(),
            sp.GetRequiredKeyedService<TenantShardResolver>(moduleKey),
            shardKey => sp.GetRequiredKeyedService<IEventStore>(shardKey)));

        services.AddKeyedScoped<IEventStoreBackend>(moduleKey, (sp, _) => new ShardRoutingEventStoreBackend(
            moduleKey,
            sp.GetRequiredService<ITenantAccessor>(),
            sp.GetRequiredKeyedService<TenantShardResolver>(moduleKey),
            shardKey => sp.GetRequiredKeyedService<IEventStoreBackend>(shardKey)));
    }
}
