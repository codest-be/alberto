using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Api.Contracts;
using Alberto.Example.Modules.Orders.Commands;

namespace Alberto.Example.Modules.Orders.Api.Endpoints;

public static class ShipOrderEndpoint
{
    public static IEndpointRouteBuilder MapShipOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{orderId:guid}/ship", async (
                Guid orderId,
                ShipOrderRequest request,
                CommandExecutor executor,
                CancellationToken ct) =>
            {
                var command = new ShipOrderCommand(orderId, request.TrackingNumber);
                var result = await executor.Execute<ShipOrderCommand, bool>(command, ct);

                return result.ToHttpResult();
            })
            .WithName("ShipOrder")
            .WithOpenApi();

        return endpoints;
    }
}