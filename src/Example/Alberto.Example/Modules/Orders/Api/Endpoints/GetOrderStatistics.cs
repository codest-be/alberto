using Alberto.Example.Modules.Orders.Projections;
using Alberto.Projections;
using Microsoft.AspNetCore.Mvc;

namespace Alberto.Example.Modules.Orders.Api.Endpoints;

public static class GetOrderStatisticsEndpoint
{
    public static IEndpointRouteBuilder MapGetOrderStatistics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders/statistics", async (
                [FromServices] IProjectionRepository<string, OrderStatistics> repository,
                CancellationToken cancellationToken) =>
            {
                var statistics = await repository.Get("global", cancellationToken);
                return statistics ?? new OrderStatistics();
            })
            .WithName("GetOrderStatistics")
            .WithTags("Orders")
            .WithOpenApi();

        return endpoints;
    }
}