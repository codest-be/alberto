using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class CreateOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b000-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b000-0000-7000-8000-000000000002");

    [Fact]
    public void Creates_an_order_that_does_not_exist_yet()
    {
        var decision = CreateOrderDecider.Decide(
            new CreateOrderState(), OrderId, CustomerId, [], notes: null);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderCreated>()
            .Which.OrderId.Should().Be(OrderId);
    }

    [Fact]
    public void Refuses_an_order_that_already_exists()
    {
        var state = new CreateOrderEvolver()
            .Apply(new CreateOrderState(), new OrderCreated(OrderId, CustomerId, [], null));

        var decision = CreateOrderDecider.Decide(state, OrderId, CustomerId, [], notes: null);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.already-exists");
    }

    [Fact]
    public void Refuses_an_order_with_no_customer()
    {
        var decision = CreateOrderDecider.Decide(
            new CreateOrderState(), OrderId, Guid.Empty, [], notes: null);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.customer-required");
    }
}
