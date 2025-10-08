using Alberto.CQRS.Registration;
using Alberto.EventSourcing;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Telemetry;
using Alberto.Example.Modules.Orders.Api.Endpoints;
using Alberto.Example.Modules.Orders.EventHandlers;
using Alberto.Example.Modules.Orders.Filters;

namespace Alberto.Example.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddEventStoreWithPostgresSubscriptions<OrderEventStore, MultiTenantContext>("orders", options =>
            {
                options.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                           throw new InvalidOperationException(
                                               "Connection string 'alberto-db' not found.");
                options.Schema = "orders";
            })
            .AddPolling(options =>
            {
                options.MinPollingIntervalMs = 100;
                options.MaxPollingIntervalMs = 2000;
                options.MaxPageSize = 100;
                options.MaxRetries = 3;
                options.RetryDelayMs = 500;
            })
            .ConfigurePipeline(pipeline => pipeline.AddConsumeFilter<LoggingFilter>())
            .AddOpenTelemetry()
            .AddSubscription<OrderProjectionSubscription>()
            .AddSubscription<OrderAnalyticsHandler>();

        services.AddCQRS(b => b.ScanAssembly(typeof(Program).Assembly));
        services.AddEventSourcedRepository<OrderState, OrderProjector, OrderEventStore>();

        return services;
    }

    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCreateOrder();
        endpoints.MapPlaceOrder();
        endpoints.MapShipOrder();
        endpoints.MapCancelOrder();
        endpoints.MapGetOrder();

        return endpoints;
    }
}