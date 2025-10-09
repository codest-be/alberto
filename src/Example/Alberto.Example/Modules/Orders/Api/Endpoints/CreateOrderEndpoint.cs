using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Api.Contracts;
using Alberto.Example.Modules.Orders.Commands;

namespace Alberto.Example.Modules.Orders.Api.Endpoints;

public static class CreateOrderEndpoint
{
    public static IEndpointRouteBuilder MapCreateOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders", async (
                CreateOrderRequest request,
                CommandExecutor executor,
                CancellationToken ct) =>
            {
                var command = new CreateOrderCommand(request.Amount, request.CustomerId);
                var result = await executor.Execute<CreateOrderCommand, Guid>(command, ct);

                return result.ToHttpResult();
            })
            .WithName("CreateOrder");

        return endpoints;
    }
}