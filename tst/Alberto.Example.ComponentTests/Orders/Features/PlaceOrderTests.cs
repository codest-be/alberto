using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Alberto.Example.Modules.Orders.Events;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Features;

public sealed class PlaceOrderTests(ITestOutputHelper testOutputHelper) : OrdersFixture(testOutputHelper)
{
    [Fact]
    public Task PlaceOrder_WhenOrderIsCreated_ShouldSucceed()
    {
        return UseCase()
            .Arrange(new CreateOrder(100m, "customer-123"))
            .Act(new PlaceOrderStep())
            .Assert(
                new HttpSuccessResponse(),
                new EventIsConsumed<OrderPlaced>().WithPredicate((sc, e) => e.OrderId == sc.GetOrder()),
                new OrderIsPlaced());
    }
}