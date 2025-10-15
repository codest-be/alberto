using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Alberto.Example.Modules.Orders.Events;
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
                new PlaceOrder())
            .Act(new ShipOrder("TRACK-123"))
            .Assert(
                new HttpSuccessResponse(),
                new EventIsConsumed<OrderShipped>().WithPredicate((sc, e) =>
                    e.OrderId == sc.GetOrderId()),
                new OrderIsShipped("TRACK-123"));
    }

    [Fact]
    public Task ShipOrder_WithEmptyTrackingNumber_ShouldFail()
    {
        return UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new PlaceOrder())
            .Act(new ShipOrder(""))
            .Assert(new HttpFailureResponse("INVALID_TRACKING_NUMBER"));
    }
}