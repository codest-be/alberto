using Alberto.Dcb;
using Alberto.Dcb.EntityFramework;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Telemetry;
using Alberto.Orders.Infrastructure.Data;
using Alberto.Orders.Infrastructure.Entities;
using Alberto.Orders.Infrastructure.Projections;
using Alberto.Orders.Infrastructure.ReadModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Orders.Infrastructure;

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
            .AddProjection(OrdersOverviewProjection.Declaration, ctx =>
            {
                var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                return () => new PostgresStateStore<OrdersOverview>(
                    dataSource,
                    nameof(OrdersOverviewProjection),
                    "orders",
                    rebuildVersion: ctx.RebuildVersion);
            })
            .AddEfProjection<OrderSummaryEntity, OrdersDbContext>(OrderSummaryEfProjection.Declaration)
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(100), BatchSize = 500 })
            .WithRebuilds());

        // Note on tenancy: the async control loop consumes every tenant's events through this
        // singleton state store, so the JSONB projection above is written without a tenant and
        // its rows carry no tenant_id. The query side currently reads it *with* one, which does
        // not match — see "The JSONB read side asks for a tenant the write side never stored"
        // in Known Gaps. Per-tenant read models go through the EF projection below, which
        // persists the tenant as a column that queries can filter on.

        return services;
    }
}
