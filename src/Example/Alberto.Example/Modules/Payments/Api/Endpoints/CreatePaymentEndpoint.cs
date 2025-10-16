using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Payments.Api.Contracts;
using Alberto.Example.Modules.Payments.Commands;

namespace Alberto.Example.Modules.Payments.Api.Endpoints;

public static class CreatePaymentEndpoint
{
    public static IEndpointRouteBuilder MapCreatePayment(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/payments", async (
                CreatePaymentRequest request,
                CommandExecutor executor,
                CancellationToken ct) =>
            {
                var command = new CreatePaymentCommand(request.OrderId, request.Amount);
                var result = await executor.Execute<CreatePaymentCommand, Guid>(command, ct);

                return result.ToHttpResult();
            })
            .WithName("CreatePayment");

        return endpoints;
    }
}