using Alberto.Example.ComponentTests.Steps.Orders;
using Alberto.Example.Modules.Orders.EventHandlers;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders;

[Collection("Service collection")]
public sealed class CreateOrderTests() : OrdersFixture()
{
    [Fact]
    public void CreateOrder_WithValidData_ShouldSucceed()
    {
        UseCase()
            .When(new CreateOrderStep(100m, "customer-123"))
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
            .When(new CreateOrderStep(0m, "customer-123"))
            .ThenExpectFailure("INVALID_AMOUNT");
    }

    [Fact]
    public void CreateOrder_WithNegativeAmount_ShouldFail()
    {
        UseCase()
            .When(new CreateOrderStep(-50m, "customer-123"))
            .ThenExpectFailure("INVALID_AMOUNT");
    }

    [Fact]
    public void CreateOrder_WithEmptyCustomerId_ShouldFail()
    {
        UseCase()
            .When(new CreateOrderStep(100m, ""))
            .ThenExpectFailure("INVALID_CUSTOMER");
    }

    [Fact]
    public void CreateOrder_ShouldPersistExactlyOneEvent()
    {
        UseCase()
            .When(new CreateOrderStep(100m, "customer-123"))
            .ThenExpectSuccess<Guid>((result, assert) => { assert.AssertEventCount(1); });
    }
}