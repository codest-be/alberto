using System.Net.Http.Json;
using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;
using Alberto.Example.Modules.Orders.Api.Contracts;

namespace Alberto.Example.ComponentTests.Orders.Steps.Orders;

public sealed class CreateOrder(decimal amount, string customerId) : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var request = new CreateOrderRequest(amount, customerId);
        var response = await scenarioContext.HttpClient().PostAsJsonAsync("/orders", request, ct);

        scenarioContext.StoreResponse(response);

        if (response.IsSuccessStatusCode)
        {
            var createdOrder = await response.Content.ReadFromJsonAsync<Guid>(cancellationToken: ct);
            scenarioContext.StoreOrderId(createdOrder);
        }
    }
}