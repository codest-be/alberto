using Alberto.CQRS;
using Alberto.EventStore;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Subscriptions.Channel;
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
                    .WithFilter<LoggingFilter>()
                    .AddPostgresProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>(
                        mode: SubscriptionMode.Hybrid)
                    .AddPostgresProjection<OrderEventStore, OrderStatisticsSubscription, string, OrderStatistics,
                        OrderStatisticsProjector>(
                        mode: SubscriptionMode.Async)
                )
                .WithCQRS(cqrs => cqrs.ScanAssembly(typeof(OrdersModule).Assembly))
                .WithTelemetry()
            );

        return services;
    }

    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCreateOrder();
        endpoints.MapPlaceOrder();
        endpoints.MapShipOrder();
        endpoints.MapCancelOrder();

        endpoints.MapGetOrder();
        endpoints.MapGetOrderStatistics();

        return endpoints;
    }
}