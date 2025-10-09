using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;

namespace Alberto.Example.ComponentTests.Orders.Steps.Orders;

public sealed class SetOrderId(Guid orderId) : IStep
{
    public ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        scenarioContext.StoreOrder(orderId);
        return ValueTask.CompletedTask;
    }
}