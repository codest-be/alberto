using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.EventHandlers;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders;

public sealed class PlaceOrderTests : OrdersFixture
{
    [Fact]
    public void PlaceOrder_WhenOrderIsCreated_ShouldSucceed()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When<PlaceOrderCommand, bool>(new PlaceOrderCommand(orderId))
            .ThenExpectSuccess(assert =>
            {
                assert.AssertEvent<OrderPlaced>(e =>
                {
                    Assert.Equal(orderId, e.OrderId);
                    Assert.Equal(100m, e.Amount);
                    Assert.Equal("customer-123", e.CustomerId);
                });
            });
    }

    [Fact]
    public void PlaceOrder_WhenOrderDoesNotExist_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .When<PlaceOrderCommand, bool>(new PlaceOrderCommand(orderId))
            .ThenExpectFailure("ORDER_NOT_FOUND");
    }

    [Fact]
    public void PlaceOrder_WhenOrderIsAlreadyPlaced_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderPlaced(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When<PlaceOrderCommand, bool>(new PlaceOrderCommand(orderId))
            .ThenExpectFailure("INVALID_ORDER_STATUS");
    }

    [Fact]
    public void PlaceOrder_WhenOrderIsCancelled_ShouldFail()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"),
                new OrderCancelled(orderId, Reason: "Customer request"))
            .When<PlaceOrderCommand, bool>(new PlaceOrderCommand(orderId))
            .ThenExpectFailure("INVALID_ORDER_STATUS");
    }

    [Fact]
    public void PlaceOrder_ShouldPersistExactlyOneEvent()
    {
        var orderId = Guid.NewGuid();

        UseCase()
            .Given(orderId,
                new OrderCreated(orderId, Amount: 100m, CustomerId: "customer-123"))
            .When<PlaceOrderCommand, bool>(new PlaceOrderCommand(orderId))
            .ThenExpectSuccess(assert =>
            {
                assert.AssertEventCount(1);
            });
    }
}