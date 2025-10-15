using System.Net.Http.Json;
using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;
using Alberto.Example.Modules.Orders.Projections;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Steps.Orders;

public class GetOrderStatistics(
    int totalOrders,
    decimal totalAmount,
    int createdCount,
    int placedCount,
    int shippedCount,
    int cancelledCount) : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var orderId = scenarioContext.GetOrderId();
        var response = await scenarioContext.HttpClient().GetAsync($"/orders/statistics", ct);

        Assert.True(response.IsSuccessStatusCode);
        var statistics = await response.Content.ReadFromJsonAsync<OrderStatistics>(cancellationToken: ct);

        Assert.NotNull(statistics);
        Assert.Equal(totalOrders, statistics.TotalOrders);
        Assert.Equal(totalAmount, statistics.TotalAmount);
        Assert.Equal(createdCount, statistics.CreatedCount);
        Assert.Equal(placedCount, statistics.PlacedCount);
        Assert.Equal(shippedCount, statistics.ShippedCount);
        Assert.Equal(cancelledCount, statistics.CancelledCount);
    }
}