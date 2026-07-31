using Alberto.Commands;
using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Telemetry;
using Alberto.Dcb.Tenancy;
using Alberto.Payments.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alberto.Payments.Platform;

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
            .WithEventsFrom(typeof(Contracts.PaymentInitiated).Assembly)
            // A single running total blended across every tenant, so it is stored under
            // TenantScope.CrossTenant rather than under any one of them. The store is built by
            // the slice, which is also what reads it.
            .AddProjection(PaymentsOverviewProjection.Declaration, PaymentsOverviewProjection.StateStore)
            // One document per payment, so one document set per tenant: the store takes the
            // tenant of the events it is given, and a reader gets back only its own tenant's
            // payments. The store is built by the slice, which is also what reads it.
            .AddProjection(PaymentSummaryProjection.Declaration, PaymentSummaryProjection.StateStore)
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(100), BatchSize = 500 }));

        return services;
    }
}
