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
            .WithPostgres(o => o with
            {
                ConnectionString = connectionString,
                AutoMigrate = true,
                Schema = "payments",
                MaxPoolSize = 30,
            })
            .WithTelemetry()
            .WithEventsFrom(typeof(Core.Events.PaymentInitiated).Assembly)
            .AddProjection(PaymentsOverviewProjection.Declaration, ctx =>
            {
                var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                return () => new PostgresStateStore<PaymentsOverview>(
                    dataSource,
                    nameof(PaymentsOverviewProjection),
                    "payments",
                    rebuildVersion: ctx.RebuildVersion);
            })
            .AddProjection(PaymentSummaryProjection.Declaration, ctx =>
            {
                var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                return () => new PostgresStateStore<PaymentSummary>(
                    dataSource,
                    nameof(PaymentSummaryProjection),
                    "payments",
                    rebuildVersion: ctx.RebuildVersion);
            })
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(100), BatchSize = 500 }));

        return services;
    }
}
