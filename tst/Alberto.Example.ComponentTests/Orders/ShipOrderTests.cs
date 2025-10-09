using Alberto.Example.ComponentTests.Steps.Orders;
using Alberto.Example.Modules.Orders.EventHandlers;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders;

public sealed class ShipOrderTests() : OrdersFixture()
{
    [Fact]
    public void ShipOrder_WhenOrderIsPlaced_ShouldSucceed()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderPlaced(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When(new ShipOrderStep(orderId, "TRACK-123"))
            .ThenExpectSuccess(assert =>
            {
                assert.AssertEvent<OrderShipped>(e =>
                {
                    Assert.Equal(orderId, e.OrderId);
                    Assert.Equal("TRACK-123", e.TrackingNumber);
                });
            });
    }

    [Fact]
    public void ShipOrder_WhenOrderDoesNotExist_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .When(new ShipOrderStep(orderId, "TRACK-123"))
            .ThenExpectFailure("ORDER_NOT_FOUND");
    }

    [Fact]
    public void ShipOrder_WhenOrderIsNotPlaced_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When(new ShipOrderStep(orderId, "TRACK-123"))
            .ThenExpectFailure("INVALID_ORDER_STATUS");
    }

    [Fact]
    public void ShipOrder_WhenOrderIsAlreadyShipped_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderPlaced(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderShipped(orderId, TrackingNumber: "TRACK-999"))
            .When(new ShipOrderStep(orderId, "TRACK-123"))
            .ThenExpectFailure("INVALID_ORDER_STATUS");
    }

    [Fact]
    public void ShipOrder_WhenOrderIsCancelled_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderCancelled(orderId, Reason: "Customer request"))
            .When(new ShipOrderStep(orderId, "TRACK-123"))
            .ThenExpectFailure("INVALID_ORDER_STATUS");
    }

    [Fact]
    public void ShipOrder_WithEmptyTrackingNumber_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderPlaced(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When(new ShipOrderStep(orderId, ""))
            .ThenExpectFailure("INVALID_TRACKING_NUMBER");
    }

    [Fact]
    public void ShipOrder_ShouldPersistExactlyOneEvent()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderPlaced(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When(new ShipOrderStep(orderId, "TRACK-123"))
            .ThenExpectSuccess(assert => { assert.AssertEventCount(1); });
    }
}