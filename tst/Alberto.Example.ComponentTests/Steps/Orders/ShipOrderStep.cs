using System.Net.Http.Json;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Orders.Api.Contracts;

namespace Alberto.Example.ComponentTests.Steps.Orders;

public sealed class ShipOrderStep(Guid orderId, string trackingNumber)
{
    public async Task<Result<bool>> ExecuteAsync(HttpClient client, CancellationToken ct = default)
    {
        var request = new ShipOrderRequest(trackingNumber);
        var response = await client.PostAsJsonAsync($"/orders/{orderId}/ship", request, ct);

        return await response.ToResult<bool>(ct);
    }
}