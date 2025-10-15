using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Alberto.Example.Modules.Orders.Events;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Features;

public sealed class CancelOrderTests(ITestOutputHelper testOutputHelper) : OrdersFixture(testOutputHelper)
{
    [Fact]
    public Task CancelOrder_WhenOrderIsCreated_ShouldSucceed()
    {
        return UseCase()
            .Arrange(new CreateOrder(100m, "customer-123"))
            .Act(new CancelOrder("Customer changed mind"))
            .Assert(
                new HttpSuccessResponse(),
                new EventIsConsumed<OrderCancelled>().WithPredicate((sc, e) => e.OrderId == sc.GetOrderId()),
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
                new EventIsConsumed<OrderCancelled>().WithPredicate((sc, e) => e.OrderId == sc.GetOrderId()),
                new OrderIsCancelled("Out of stock"));
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