using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Commands;

namespace Alberto.Example.Modules.Orders.Api.Endpoints;

public static class PlaceOrderEndpoint
{
    public static IEndpointRouteBuilder MapPlaceOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{orderId:guid}/place", async (
                Guid orderId,
                CommandExecutor executor,
                CancellationToken ct) =>
            {
                var command = new PlaceOrderCommand(orderId);
                var result = await executor.Execute<PlaceOrderCommand, bool>(command, ct);

                return result.ToHttpResult();
            })
            .WithName("PlaceOrder")
            .WithOpenApi();

        return endpoints;
    }
}