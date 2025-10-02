using EventStore;
using EventStore.Postgres;
using EventStore.Telemetry;

namespace Alberto.Example.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
        => services
            .AddEventStore()
            .AddPostgresEventStore(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                     throw new InvalidOperationException("Connection string 'alberto-db' not found.");
                o.Schema = "orders";
            })
            .AddTelemetry()
            .Services;
}