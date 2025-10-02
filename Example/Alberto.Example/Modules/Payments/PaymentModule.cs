using EventStore;
using EventStore.Postgres;
using EventStore.Telemetry;

namespace Alberto.Example.Modules.Payments;

public static class PaymentModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
        => services
            .AddEventStore()
            .AddPostgresEventStore(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                     throw new InvalidOperationException("Connection string 'alberto-db' not found.");
                o.Schema = "payments";
            })
            .AddTelemetry()
            .Services;
}