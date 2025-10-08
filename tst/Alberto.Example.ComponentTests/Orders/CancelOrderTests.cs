using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.EventHandlers;
using Xunit;

namespace Alberto.Example.IntegrationTests.Orders;

public sealed class CancelOrderTests : OrdersFixture
{
    [Fact]
    public void CancelOrder_WhenOrderIsCreated_ShouldSucceed()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When<CancelOrderCommand, bool>(new CancelOrderCommand(orderId, "Customer changed mind"))
            .ThenExpectSuccess(assert =>
            {
                assert.AssertEvent<OrderCancelled>(e =>
                {
                    Assert.Equal(orderId, e.OrderId);
                    Assert.Equal("Customer changed mind", e.Reason);
                });
            });
    }

    [Fact]
    public void CancelOrder_WhenOrderIsPlaced_ShouldSucceed()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderPlaced(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When<CancelOrderCommand, bool>(new CancelOrderCommand(orderId, "Out of stock"))
            .ThenExpectSuccess(assert =>
            {
                assert.AssertEvent<OrderCancelled>(e =>
                {
                    Assert.Equal(orderId, e.OrderId);
                    Assert.Equal("Out of stock", e.Reason);
                });
            });
    }

    [Fact]
    public void CancelOrder_WhenOrderDoesNotExist_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .When<CancelOrderCommand, bool>(new CancelOrderCommand(orderId, "Some reason"))
            .ThenExpectFailure("ORDER_NOT_FOUND");
    }

    [Fact]
    public void CancelOrder_WhenOrderIsAlreadyCancelled_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderCancelled(orderId, Reason: "First cancellation"))
            .When<CancelOrderCommand, bool>(new CancelOrderCommand(orderId, "Second cancellation"))
            .ThenExpectFailure("ORDER_ALREADY_CANCELLED");
    }

    [Fact]
    public void CancelOrder_WhenOrderIsShipped_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderPlaced(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderShipped(orderId, TrackingNumber: "TRACK-123"))
            .When<CancelOrderCommand, bool>(new CancelOrderCommand(orderId, "Changed mind"))
            .ThenExpectFailure("CANNOT_CANCEL_SHIPPED_ORDER");
    }

    [Fact]
    public void CancelOrder_WithEmptyReason_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When<CancelOrderCommand, bool>(new CancelOrderCommand(orderId, ""))
            .ThenExpectFailure("INVALID_REASON");
    }

    [Fact]
    public void CancelOrder_ShouldPersistExactlyOneEvent()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When<CancelOrderCommand, bool>(new CancelOrderCommand(orderId, "Customer request"))
            .ThenExpectSuccess(assert =>
            {
                assert.AssertEventCount(1);
            });
    }
}