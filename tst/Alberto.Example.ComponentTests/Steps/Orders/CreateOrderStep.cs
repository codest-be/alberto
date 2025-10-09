using System.Net.Http.Json;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Api.Contracts;

namespace Alberto.Example.ComponentTests.Steps.Orders;

public sealed class CreateOrderStep(decimal amount, string customerId)
{
    public async Task<Result<Guid>> ExecuteAsync(HttpClient client, CancellationToken ct = default)
    {
        var request = new CreateOrderRequest(amount, customerId);
        var response = await client.PostAsJsonAsync("/orders", request, ct);

        return await response.ToResult<Guid>(ct);
    }
}