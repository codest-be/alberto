using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Api.Contracts;
using Alberto.Example.Modules.Orders.Commands;

namespace Alberto.Example.Modules.Orders.Api.Endpoints;

public static class CancelOrderEndpoint
{
    public static IEndpointRouteBuilder MapCancelOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{orderId:guid}/cancel", async (
                Guid orderId,
                CancelOrderRequest request,
                CommandExecutor executor,
                CancellationToken ct) =>
            {
                var command = new CancelOrderCommand(orderId, request.Reason);
                var result = await executor.Execute(command, ct);

                return result.ToHttpResult();
            })
            .WithName("CancelOrder")
            .WithOpenApi();

        return endpoints;
    }
}