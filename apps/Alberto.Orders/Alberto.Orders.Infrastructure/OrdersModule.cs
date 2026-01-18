using Alberto.Dcb;
using Alberto.Dcb.Admin;
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
            .WithPostgres(options =>
            {
                options.ConnectionString = connectionString;
                options.AutoMigrate = true;
                options.Schema = "orders";
                options.MaxPoolSize = 30;
            })
            .WithEntityFramework<OrdersDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
            })
            .WithTelemetry()
            .WithConsumer(consumer => consumer
                .WithTenantDistribution()
                .WithPollingInterval(TimeSpan.FromMilliseconds(250))
                .WithBatchSize(100)
                .WithErrorPolicy(policy => policy
                    .MaxRetries(3)
                    .RetryDelay(TimeSpan.FromSeconds(1))
                    .DeadLetterOnMaxRetries(true))
                // JSONB projection for overview (aggregate stats)
                .AddProjection<OrdersOverview, OrdersOverviewProjection>(sp =>
                {
                    var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                    return tenantId => new PostgresStateStore<OrdersOverview>(
                        dataSource,
                        tenantId,
                        nameof(OrdersOverviewProjection),
                        "orders");
                })
                // EF projection for order summaries (enables filtering/querying)
                .AddEfProjection<OrderSummaryEntity, OrderSummaryEfProjection, OrdersDbContext>())
            .WithAdmin(admin =>
            {
                admin.Title = "Orders Admin";
            }));

        // Note: Query-side state stores are created dynamically per-tenant in GraphQL queries.
        // The projection state stores above use the tenant from the event envelope during writes.

        return services;
    }
}
