using Alberto.CQRS.Queries;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Payments.Api.Contracts;
using Alberto.Example.Modules.Payments.Queries;

namespace Alberto.Example.Modules.Payments.Api.Endpoints;

public static class GetPaymentEndpoint
{
    public static IEndpointRouteBuilder MapGetPayment(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/payments/{id:guid}", async (
                Guid id,
                QueryExecutor executor,
                CancellationToken ct) =>
            {
                var query = new GetPaymentQuery(id);
                var result = await executor.Execute<GetPaymentQuery, PaymentDto>(query, ct);

                return result.ToHttpResult();
            })
            .WithName("GetPayment");

        return endpoints;
    }
}