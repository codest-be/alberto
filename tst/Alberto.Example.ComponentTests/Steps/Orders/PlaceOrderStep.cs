using Alberto.CQRS.Results;

namespace Alberto.Example.ComponentTests.Steps.Orders;

public sealed class PlaceOrderStep(Guid orderId)
{
    public async Task<Result<bool>> ExecuteAsync(HttpClient client, CancellationToken ct = default)
    {
        var response = await client.PostAsync($"/orders/{orderId}/place", null, ct);

        return await response.ToResult<bool>(ct);
    }
}