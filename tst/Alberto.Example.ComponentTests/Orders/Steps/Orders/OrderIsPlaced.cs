using System.Net.Http.Json;
using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;
using Alberto.Example.Modules.Orders.Api.Contracts;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Steps.Orders;

public class OrderIsPlaced : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var orderId = scenarioContext.GetOrder();
        var response = await scenarioContext.HttpClient().GetAsync($"/orders/{orderId}", ct);

        Assert.True(response.IsSuccessStatusCode, "Could not get order");

        var order = await response.Content.ReadFromJsonAsync<OrderDto>(cancellationToken: ct);

        Assert.NotNull(order);
        Assert.Equal("Placed", order.Status);
    }
}