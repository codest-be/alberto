using Alberto.CQRS;
using Alberto.CQRS.Telemetry;
using Alberto.EventStore;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Telemetry;
using Alberto.Example.Modules.Orders.Api.Endpoints;
using Alberto.Example.Modules.Orders.Filters;
using Alberto.Example.Modules.Orders.Projections;
using Alberto.Projections.InMemory;

namespace Alberto.Example.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddModule<OrderEventStore>("orders", module => module
                .WithPostgres(options =>
                {
                    var baseConnectionString = configuration.GetConnectionString("alberto-db") ??
                                               throw new InvalidOperationException(
                                                   "Connection string 'alberto-db' not found.");

                    // Add connection pooling parameters for better resource management
                    options.ConnectionString =
                        $"{baseConnectionString};Minimum Pool Size=5;Maximum Pool Size=30;Connection Idle Lifetime=300;Connection Pruning Interval=10";
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
                    .AddInMemoryProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>(
                        mode: SubscriptionMode.Hybrid)
                    .AddInMemoryProjection<OrderEventStore, OrderStatisticsSubscription, string, OrderStatistics,
                        OrderStatisticsProjector>(
                        mode: SubscriptionMode.Async)
                )
                .WithCQRS(cqrs => cqrs
                    .ScanAssembly(typeof(OrdersModule).Assembly)
                    .WithTelemetry())
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