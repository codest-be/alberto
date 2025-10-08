using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.EventHandlers;
using Xunit;

namespace Alberto.Example.IntegrationTests.Orders;

public sealed class CreateOrderTests : OrdersFixture
{
    [Fact]
    public void CreateOrder_WithValidData_ShouldSucceed()
    {
        UseCase()
            .When<CreateOrderCommand, Guid>(new CreateOrderCommand(100m, "customer-123"))
            .ThenExpectSuccess<Guid>((result, assert) =>
            {
                Assert.NotEqual(Guid.Empty, result);

                assert.AssertEvent<OrderCreated>(e =>
                {
                    Assert.Equal(result, e.OrderId);
                    Assert.Equal(100m, e.Amount);
                    Assert.Equal("customer-123", e.CustomerId);
                });
            });
    }

    [Fact]
    public void CreateOrder_WithZeroAmount_ShouldFail()
    {
        UseCase()
            .When<CreateOrderCommand, Guid>(new CreateOrderCommand(0m, "customer-123"))
            .ThenExpectFailure("INVALID_AMOUNT");
    }

    [Fact]
    public void CreateOrder_WithNegativeAmount_ShouldFail()
    {
        UseCase()
            .When<CreateOrderCommand, Guid>(new CreateOrderCommand(-50m, "customer-123"))
            .ThenExpectFailure("INVALID_AMOUNT");
    }

    [Fact]
    public void CreateOrder_WithEmptyCustomerId_ShouldFail()
    {
        UseCase()
            .When<CreateOrderCommand, Guid>(new CreateOrderCommand(100m, ""))
            .ThenExpectFailure("INVALID_CUSTOMER");
    }

    [Fact]
    public void CreateOrder_ShouldPersistExactlyOneEvent()
    {
        UseCase()
            .When<CreateOrderCommand, Guid>(new CreateOrderCommand(100m, "customer-123"))
            .ThenExpectSuccess<Guid>((result, assert) =>
            {
                assert.AssertEventCount(1);
            });
    }
}