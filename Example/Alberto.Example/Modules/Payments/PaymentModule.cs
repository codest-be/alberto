using Alberto.EventStore;
using Alberto.EventStore.Postgres;

namespace Alberto.Example.Modules.Payments;

public static class PaymentModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
        => services
            .AddPostgresEventStore<PaymentEventStore>(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                     throw new InvalidOperationException("Connection string 'alberto-db' not found.");
                o.Schema = "payments";
            });
}

public class PaymentEventStore(EventStoreFactory factory) : EventStore.EventStore(factory);