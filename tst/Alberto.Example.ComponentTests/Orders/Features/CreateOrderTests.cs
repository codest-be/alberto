using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Xunit;
using OrderCreated = Alberto.Example.Modules.Orders.Events.OrderCreated;

namespace Alberto.Example.ComponentTests.Orders.Features;

public sealed class CreateOrderTests(ITestOutputHelper testOutputHelper) : OrdersFixture(testOutputHelper)
{
    [Fact]
    public Task CreateOrder_WithValidData_ShouldSucceed()
    {
        return UseCase()
            .Act(new CreateOrder(100m, "customer-123"))
            .Assert(
                new HttpSuccessResponse(),
                new EventIsConsumed<OrderCreated>().WithPredicate((sc, e) => e.OrderId == sc.GetOrderId()),
                new Steps.Orders.OrderCreated(100m, "customer-123"));
    }

    [Fact]
    public Task CreateOrder_WithZeroAmount_ShouldFail()
    {
        return UseCase()
            .Act(new CreateOrder(0m, "customer-123"))
            .Assert(new HttpFailureResponse("INVALID_AMOUNT"));
    }

    [Fact]
    public Task CreateOrder_WithNegativeAmount_ShouldFail()
    {
        return UseCase()
            .Act(new CreateOrder(-50m, "customer-123"))
            .Assert(new HttpFailureResponse("INVALID_AMOUNT"));
    }

    [Fact]
    public Task CreateOrder_WithEmptyCustomerId_ShouldFail()
    {
        return UseCase()
            .Act(new CreateOrder(100m, ""))
            .Assert(new HttpFailureResponse("INVALID_CUSTOMER"));
    }
}