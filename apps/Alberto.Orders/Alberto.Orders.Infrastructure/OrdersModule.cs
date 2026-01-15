using Alberto.Dcb;
using Alberto.Dcb.Admin;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Telemetry;
using Alberto.Orders.Infrastructure.Projections;
using Alberto.Orders.Infrastructure.ReadModels;
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
        var connectionString = configuration.GetConnectionString("orders")
            ?? throw new InvalidOperationException("Connection string 'orders' not found");

        services.AddAlberto(ModuleKey, builder => builder
            .WithPostgres(options =>
            {
                options.ConnectionString = connectionString;
                options.AutoMigrate = true;
            })
            .WithTelemetry()
            .WithConsumer(consumer => consumer
                .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                .WithBatchSize(100)
                .WithErrorPolicy(policy => policy
                    .MaxRetries(3)
                    .RetryDelay(TimeSpan.FromSeconds(1))
                    .DeadLetterOnMaxRetries(true))
                .AddProjection<OrdersOverview, OrdersOverviewProjection>(sp =>
                {
                    var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                    return new PostgresStateStore<OrdersOverview>(
                        dataSource,
                        "default",
                        nameof(OrdersOverviewProjection));
                })
                .AddProjection<OrderSummary, OrderSummaryProjection>(sp =>
                {
                    var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                    return new PostgresStateStore<OrderSummary>(
                        dataSource,
                        "default",
                        nameof(OrderSummaryProjection));
                }))
            .WithAdmin(admin =>
            {
                admin.Title = "Orders Admin";
            }));

        // Register state store for querying OrdersOverview
        services.AddSingleton(sp =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
            return new PostgresStateStore<OrdersOverview>(
                dataSource,
                "default",
                nameof(OrdersOverviewProjection));
        });

        // Register state store for querying OrderSummary (recent orders)
        services.AddSingleton(sp =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
            return new PostgresStateStore<OrderSummary>(
                dataSource,
                "default",
                nameof(OrderSummaryProjection));
        });

        return services;
    }
}
