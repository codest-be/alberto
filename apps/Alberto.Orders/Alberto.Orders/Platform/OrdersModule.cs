using Alberto.Dcb;
using Alberto.Dcb.EntityFramework;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Telemetry;
using Alberto.Dcb.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Orders.Platform;

/// <summary>
/// DI registration for the Orders module.
/// </summary>
public static class OrdersModule
{
    public const string ModuleKey = "orders";

    /// <summary>
    /// Adds the Orders module to the service collection.
    /// </summary>
    public static IServiceCollection AddOrdersModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("alberto")
            ?? throw new InvalidOperationException("Connection string 'alberto' not found");

        services.AddAlberto(ModuleKey, builder => builder
            .WithTenancy()
            .WithPostgres(o => o with
            {
                ConnectionString = connectionString,
                AutoMigrate = false, // Migrations run in Alberto.Orders.Migrations (Aspire sequencing)
                Schema = "orders",
                MaxPoolSize = 30,
            })
            .WithEntityFramework<OrdersDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
            })
            .WithTelemetry()
            .WithEventsFrom(typeof(Contracts.OrderCreated).Assembly)
            // A single overview blended across every tenant, so it is stored under
            // TenantScope.CrossTenant rather than under any one of them.
            .AddProjection(OrdersOverviewProjection.Declaration, ctx =>
            {
                var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                return tenantId => new PostgresStateStore<OrdersOverview>(
                    dataSource,
                    nameof(OrdersOverviewProjection),
                    "orders",
                    rebuildVersion: ctx.RebuildVersion,
                    tenantId: TenantScope.CrossTenantFor(tenantId));
            })
            .AddEfProjection<OrderSummaryEntity, OrdersDbContext>(OrderSummaryEfProjection.Declaration)
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(100), BatchSize = 500 })
            .WithRebuilds());

        // Note on tenancy: the async control loop consumes every tenant's events, but a state
        // store's tenancy is fixed when it is built, so the consumer builds one store per tenant
        // and hands each the tenant it belongs to. OrdersOverview blends every tenant into one
        // document and therefore stores it under TenantScope.CrossTenant; the EF projection
        // below is per-tenant and persists the tenant as a column that queries filter on.

        return services;
    }
}
