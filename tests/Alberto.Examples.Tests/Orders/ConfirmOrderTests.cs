using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class ConfirmOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b003-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ConfirmOrderState Created(params OrderLineItem[] items) =>
        new ConfirmOrderEvolver().Apply(
            new ConfirmOrderState(),
            new OrderCreated(OrderId, Guid.NewGuid(), items, null));

    [Fact]
    public void Confirms_a_draft_order_that_has_items()
    {
        var state = Created(new OrderLineItem(Guid.NewGuid(), "Widget", 1, 9.99m));

        var decision = ConfirmOrderDecider.Decide(state, Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderConfirmed>();
    }

    [Fact]
    public void Refuses_an_empty_order_with_the_empty_problem_not_the_status_one()
    {
        var decision = ConfirmOrderDecider.Decide(Created(), Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.empty");
    }

    [Fact]
    public void Refuses_an_order_that_is_already_confirmed()
    {
        var evolver = new ConfirmOrderEvolver();
        var state = evolver.Apply(
            Created(new OrderLineItem(Guid.NewGuid(), "Widget", 1, 9.99m)),
            new OrderConfirmed(OrderId, Now));

        var decision = ConfirmOrderDecider.Decide(state, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.invalid-status");
    }
}
