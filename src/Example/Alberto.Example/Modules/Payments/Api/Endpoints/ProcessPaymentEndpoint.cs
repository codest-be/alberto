using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Payments.Commands;

namespace Alberto.Example.Modules.Payments.Api.Endpoints;

public static class ProcessPaymentEndpoint
{
    public static IEndpointRouteBuilder MapProcessPayment(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/payments/{id:guid}/process", async (
                Guid id,
                CommandExecutor executor,
                CancellationToken ct) =>
            {
                var command = new ProcessPaymentCommand(id);
                var result = await executor.Execute<ProcessPaymentCommand, bool>(command, ct);

                return result.ToHttpResult();
            })
            .WithName("ProcessPayment");

        return endpoints;
    }
}