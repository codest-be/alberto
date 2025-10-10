using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;
using Alberto.Example.Modules.Orders.Projections;
using Alberto.Projections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Steps.Subscriptions;

public sealed class OrderExistsInReadModel(Guid orderId, string expectedStatus) : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var repository = scenarioContext.GetRequiredKeyedService<IProjectionRepository<Guid, Order>>("orders");

        var order = await repository.Get(orderId, ct);

        Assert.NotNull(order);
        Assert.Equal(orderId, order.OrderId);
        Assert.Equal(expectedStatus, order.Status.ToString());
    }
}