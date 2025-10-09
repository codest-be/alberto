using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Features;

public sealed class ShipOrderTests(ITestOutputHelper testOutputHelper) : OrdersFixture(testOutputHelper)
{
    [Fact]
    public Task ShipOrder_WhenOrderIsPlaced_ShouldSucceed()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new PlaceOrderStep())
            .Act(new ShipOrderStep("TRACK-123"))
            .Assert(
                new HttpSuccessResponse(),
                new OrderIsShipped("TRACK-123"));
    }

    [Fact]
    public Task ShipOrder_WhenOrderDoesNotExist_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        return UseCase()
            .Arrange(new SetOrderId(orderId))
            .Act(new ShipOrderStep("TRACK-123"))
            .Assert(new HttpFailureResponse("ORDER_NOT_FOUND"));
    }

    [Fact]
    public Task ShipOrder_WhenOrderIsNotPlaced_ShouldFail()
    {
        return UseCase()
            .Arrange(new CreateOrder(100m, "customer-123"))
            .Act(new ShipOrderStep("TRACK-123"))
            .Assert(new HttpFailureResponse("INVALID_ORDER_STATUS"));
    }

    [Fact]
    public Task ShipOrder_WhenOrderIsAlreadyShipped_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new PlaceOrderStep(),
                new ShipOrderStep("TRACK-123"))
            .Act(new ShipOrderStep("TRACK-456"))
            .Assert(new HttpFailureResponse("INVALID_ORDER_STATUS"));
    }

    [Fact]
    public Task ShipOrder_WhenOrderIsCancelled_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new CancelOrder("Customer request"))
            .Act(new ShipOrderStep("TRACK-123"))
            .Assert(new HttpFailureResponse("INVALID_ORDER_STATUS"));
    }

    [Fact]
    public Task ShipOrder_WithEmptyTrackingNumber_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new PlaceOrderStep())
            .Act(new ShipOrderStep(""))
            .Assert(new HttpFailureResponse("INVALID_TRACKING_NUMBER"));
    }
}