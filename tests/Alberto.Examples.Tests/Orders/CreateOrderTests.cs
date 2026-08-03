using Alberto.Orders.Contracts;
using Alberto.Orders.Features;

namespace Alberto.Examples.Tests.Orders;

public sealed class CreateOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b000-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b000-0000-7000-8000-000000000002");

    [Fact]
    public void Creates_an_order_that_does_not_exist_yet()
    {
        Spec.For(new CreateOrderEvolver())
            .GivenNoEvents()
            .When(state => CreateOrderDecider.Decide(state, OrderId, CustomerId, [], notes: null))
            .ThenEmitsOnly<OrderCreated>(e => e.OrderId == OrderId);
    }

    [Fact]
    public void Refuses_an_order_that_already_exists()
    {
        Spec.For(new CreateOrderEvolver())
            .Given(new OrderCreated(OrderId, CustomerId, [], null))
            .When(state => CreateOrderDecider.Decide(state, OrderId, CustomerId, [], notes: null))
            .ThenFails(OrderProblems.AlreadyExists(OrderId));
    }

    [Fact]
    public void Refuses_an_order_with_no_customer()
    {
        Spec.For(new CreateOrderEvolver())
            .GivenNoEvents()
            .When(state => CreateOrderDecider.Decide(state, OrderId, Guid.Empty, [], notes: null))
            .ThenFails(OrderProblems.CustomerRequired());
    }
}
