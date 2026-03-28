using Alberto.Dcb;
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
            .WithTenancy()
            .WithPostgres(options =>
            {
                options.ConnectionString = connectionString;
                options.AutoMigrate = true;
                options.Schema = "payments";
                options.MaxPoolSize = 30;
            })
            .WithTelemetry()
            .AddProjection(PaymentsOverviewProjection.Declaration, sp =>
            {
                var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                return () => new PostgresStateStore<PaymentsOverview>(
                    dataSource,
                    nameof(PaymentsOverviewProjection),
                    "payments");
            })
            .AddProjection(PaymentSummaryProjection.Declaration, sp =>
            {
                var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                return () => new PostgresStateStore<PaymentSummary>(
                    dataSource,
                    nameof(PaymentSummaryProjection),
                    "payments");
            })
            .WithControlLoop(loop => loop
                .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                .WithBatchSize(500)));

        return services;
    }
}
