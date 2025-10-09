using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
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
                new OrderIsPlaced());
    }
}