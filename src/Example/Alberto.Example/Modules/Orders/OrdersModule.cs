using Alberto.CQRS;
using Alberto.EventSourcing.Projections;
using Alberto.EventStore;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Telemetry;
using Alberto.Example.Modules.Orders.Api.Endpoints;
using Alberto.Example.Modules.Orders.Filters;
using Alberto.Example.Modules.Orders.Projections;
using Alberto.Projections.Postgres;

namespace Alberto.Example.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddModule<OrderEventStore>("orders", module => module
                .WithPostgres(options =>
                {
                    options.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                               throw new InvalidOperationException(
                                                   "Connection string 'alberto-db' not found.");
                    options.Schema = "orders";
                })
                .WithMultiTenancy<MultiTenantContext>()
                .WithPollingSubscriptions(polling => polling
                    .Configure(options =>
                    {
                        options.MinPollingIntervalMs = 100;
                        options.MaxPollingIntervalMs = 2000;
                        options.MaxPageSize = 100;
                        options.MaxRetries = 3;
                        options.RetryDelayMs = 500;
                    })
                    .WithFilter<LoggingFilter>()
                    .AddProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>()
                )
                .WithCQRS(cqrs => cqrs.ScanAssembly(typeof(OrdersModule).Assembly))
                .WithTelemetry()
            );

        // Register projection repository separately for now
        // TODO: Could be integrated into .AddProjection<>() in the future
        services.AddPostgresProjectionRepository<Guid, Order, OrderProjector>("orders");

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