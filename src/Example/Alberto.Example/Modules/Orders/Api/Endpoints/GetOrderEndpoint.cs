using Alberto.CQRS.Queries;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Api.Contracts;
using Alberto.Example.Modules.Orders.Queries;

namespace Alberto.Example.Modules.Orders.Api.Endpoints;

public static class GetOrderEndpoint
{
    public static IEndpointRouteBuilder MapGetOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders/{orderId:guid}", async (
                Guid orderId,
                QueryExecutor executor,
                CancellationToken ct) =>
            {
                var query = new GetOrderQuery(orderId);
                var result = await executor.Execute<GetOrderQuery, OrderDto>(query, ct);

                return result.ToHttpResult();
            })
            .WithName("GetOrder")
            .WithOpenApi();

        return endpoints;
    }
}