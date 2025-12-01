using Alberto.CQRS;
using Alberto.CQRS.Telemetry;
using Alberto.EventStore;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Telemetry;
using Alberto.Example.Modules.Payments.Api.Endpoints;
using Alberto.Example.Modules.Payments.Projections;
using Alberto.Projections.InMemory;

namespace Alberto.Example.Modules.Payments;

public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddModule<PaymentEventStore>("payments", module => module
                .WithPostgres(options =>
                {
                    var baseConnectionString = configuration.GetConnectionString("alberto-db") ??
                                               throw new InvalidOperationException(
                                                   "Connection string 'alberto-db' not found.");

                    // Add connection pooling parameters for better resource management
                    options.ConnectionString =
                        $"{baseConnectionString};Minimum Pool Size=5;Maximum Pool Size=30;Connection Idle Lifetime=300;Connection Pruning Interval=10";
                    options.Schema = "payments";

                    // Store migrations within the Payments module directory
                    options.MigrationsDirectory = "Modules/Payments/Migrations";
                })
                .WithMultiTenancy<MultiTenantContext>()
                .WithChannelSubscriptions(channel => channel
                    .ConfigureSync(options =>
                    {
                        options.MaxRetries = 3;
                        options.RetryDelayMs = 250;
                        options.AllowParallelExecution = true;
                    })
                    .ConfigureAsync(options =>
                    {
                        options.MinPollingIntervalMs = 100;
                        options.PollingGrowFactor = 1.5;
                        options.MaxRetries = 5;
                        options.RetryDelayMs = 250;
                        options.MaxPageSize = 100;
                    })
                    .AddInMemoryProjection<PaymentProjectionSubscription, PaymentProjector, PaymentEventStore>(
                        mode: SubscriptionMode.Hybrid)
                )
                .WithCQRS(cqrs => cqrs
                    .ScanAssembly(typeof(PaymentsModule).Assembly)
                    .WithTelemetry())
                .WithTelemetry()
            );

        return services;
    }

    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCreatePayment();
        endpoints.MapProcessPayment();
        endpoints.MapGetPayment();

        return endpoints;
    }
}