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
            .WithPostgres(options =>
            {
                options.ConnectionString = connectionString;
                options.AutoMigrate = false; // Migrations run in Alberto.Orders.Migrations (Aspire sequencing)
                options.Schema = "orders";
                options.MaxPoolSize = 30;
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
            .WithControlLoop(loop => loop
                .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                .WithBatchSize(500)
                .WithRebuilds()));

        // Note: Query-side state stores are created dynamically per-tenant in GraphQL queries.
        // The projection state stores above use the tenant from the event envelope during writes.

        return services;
    }
}
