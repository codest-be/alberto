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

    [Fact]
    public Task PlaceOrder_WhenOrderDoesNotExist_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        return UseCase()
            .Arrange(new SetOrderId(orderId))
            .Act(new PlaceOrderStep())
            .Assert(new HttpFailureResponse("ORDER_NOT_FOUND"));
    }

    [Fact]
    public Task PlaceOrder_WhenOrderIsAlreadyPlaced_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new PlaceOrderStep())
            .Act(new PlaceOrderStep())
            .Assert(new HttpFailureResponse("INVALID_ORDER_STATUS"));
    }

    [Fact]
    public Task PlaceOrder_WhenOrderIsCancelled_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new CancelOrder("Customer request"))
            .Act(new PlaceOrderStep())
            .Assert(new HttpFailureResponse("INVALID_ORDER_STATUS"));
    }
}