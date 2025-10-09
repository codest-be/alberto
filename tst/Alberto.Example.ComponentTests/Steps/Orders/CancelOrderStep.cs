using System.Net.Http.Json;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Api.Contracts;

namespace Alberto.Example.ComponentTests.Steps.Orders;

public sealed class CancelOrderStep(Guid orderId, string reason)
{
    public async Task<Result<bool>> ExecuteAsync(HttpClient client, CancellationToken ct = default)
    {
        var request = new CancelOrderRequest(reason);
        var response = await client.PostAsJsonAsync($"/orders/{orderId}/cancel", request, ct);

        return await response.ToResult<bool>(ct);
    }
}