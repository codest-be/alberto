using System.Net.Http.Json;
using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;
using Alberto.Example.Modules.Orders.Api.Contracts;

namespace Alberto.Example.ComponentTests.Orders.Steps.Orders;

public sealed class ShipOrderStep(string trackingNumber) : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var orderId = scenarioContext.GetOrderId();
        var request = new ShipOrderRequest(trackingNumber);
        var response = await scenarioContext.HttpClient().PostAsJsonAsync($"/orders/{orderId}/ship", request, ct);

        scenarioContext.StoreResponse(response);
    }
}