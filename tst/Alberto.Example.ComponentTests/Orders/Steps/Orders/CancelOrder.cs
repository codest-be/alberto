using System.Net.Http.Json;
using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;
using Alberto.Example.Modules.Orders.Api.Contracts;

namespace Alberto.Example.ComponentTests.Orders.Steps.Orders;

public sealed class CancelOrder(string reason) : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var orderId = scenarioContext.GetOrderId();
        var request = new CancelOrderRequest(reason);
        var response = await scenarioContext.HttpClient().PostAsJsonAsync($"/orders/{orderId}/cancel", request, ct);

        scenarioContext.StoreResponse(response);
    }
}