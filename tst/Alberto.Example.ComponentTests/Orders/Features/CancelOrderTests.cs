using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Features;

public sealed class CancelOrderTests(ITestOutputHelper testOutputHelper) : OrdersFixture(testOutputHelper)
{
    [Fact]
    public Task CancelOrder_WhenOrderIsCreated_ShouldSucceed()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"))
            .Act(new CancelOrder("Customer changed mind"))
            .Assert(
                new HttpSuccessResponse(),
                new OrderIsCancelled("Customer changed mind"));
    }

    [Fact]
    public Task CancelOrder_WhenOrderIsPlaced_ShouldSucceed()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new PlaceOrderStep())
            .Act(new CancelOrder("Out of stock"))
            .Assert(
                new HttpSuccessResponse(),
                new OrderIsCancelled("Out of stock"));
    }

    [Fact]
    public Task CancelOrder_WhenOrderDoesNotExist_ShouldFail()
    {
        return UseCase()
            .Arrange(new SetOrderId(Guid.NewGuid()))
            .Act(new CancelOrder("Some reason"))
            .Assert(new HttpFailureResponse("ORDER_NOT_FOUND"));
    }

    [Fact]
    public Task CancelOrder_WhenOrderIsAlreadyCancelled_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new CancelOrder("First cancellation"))
            .Act(new CancelOrder("Second cancellation"))
            .Assert(new HttpFailureResponse("ORDER_ALREADY_CANCELLED"));
    }

    [Fact]
    public Task CancelOrder_WhenOrderIsShipped_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new PlaceOrderStep(),
                new ShipOrderStep("TRACK-123"))
            .Act(new CancelOrder("Changed mind"))
            .Assert(new HttpFailureResponse("CANNOT_CANCEL_SHIPPED_ORDER"));
    }

    [Fact]
    public Task CancelOrder_WithEmptyReason_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"))
            .Act(new CancelOrder(""))
            .Assert(new HttpFailureResponse("INVALID_REASON"));
    }
}