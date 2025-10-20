using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;

namespace Alberto.Example.ComponentTests.Orders.Steps.Orders;

public sealed class PlaceOrder : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var orderId = scenarioContext.GetOrderId();
        var response = await scenarioContext.HttpClientWithTenant().PostAsync($"/orders/{orderId}/place", null, ct);

        scenarioContext.StoreResponse(response);
    }
}