using Alberto.Dcb;
using Alberto.Dcb.Admin;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Telemetry;
using Alberto.Payments.Infrastructure.Projections;
using Alberto.Payments.Infrastructure.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Payments.Infrastructure;

/// <summary>
/// DI registration for the Payments module.
/// </summary>
public static class PaymentsModule
{
    public const string ModuleKey = "payments";

    /// <summary>
    /// Adds the Payments module to the service collection.
    /// </summary>
    public static IServiceCollection AddPaymentsModule(
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
                options.Schema = "payments";
                options.MaxPoolSize = 30;
            })
            .WithTelemetry()
            .WithConsumer(consumer => consumer
                .WithTenantDistribution()
                .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                .WithBatchSize(500)
                .WithErrorPolicy(policy => policy
                    .MaxRetries(3)
                    .RetryDelay(TimeSpan.FromSeconds(1))
                    .DeadLetterOnMaxRetries(true))
                .AddProjection<PaymentsOverview, PaymentsOverviewProjection>(sp =>
                {
                    var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                    return tenantId => new PostgresStateStore<PaymentsOverview>(
                        dataSource,
                        tenantId,
                        nameof(PaymentsOverviewProjection),
                        "payments");
                })
                .AddProjection<PaymentSummary, PaymentSummaryProjection>(sp =>
                {
                    var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                    return tenantId => new PostgresStateStore<PaymentSummary>(
                        dataSource,
                        tenantId,
                        nameof(PaymentSummaryProjection),
                        "payments");
                }))
            .WithAdmin(admin =>
            {
                admin.Title = "Payments";
                // admin.ReadOnly = true;  // Uncomment to make this module read-only in the UI
            }));

        return services;
    }
}
