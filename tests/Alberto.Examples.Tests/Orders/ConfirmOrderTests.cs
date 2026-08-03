using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class ConfirmOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b003-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b003-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static OrderCreated CreatedWith(params OrderLineItem[] items) =>
        new(OrderId, CustomerId, items, null);

    private static readonly OrderLineItem Widget =
        new(Guid.Parse("0197b003-0000-7000-8000-000000000003"), "Widget", 1, 9.99m);

    [Fact]
    public void Confirms_a_draft_order_that_has_items()
    {
        Spec.For(new ConfirmOrderEvolver())
            .Given(CreatedWith(Widget))
            .When(state => ConfirmOrderDecider.Decide(state, Now))
            .ThenEmitsOnly<OrderConfirmed>(e => e.OrderId == OrderId)
            .ThenState(s => s.Status.Should().Be(OrderStatus.Confirmed));
    }

    [Fact]
    public void Refuses_an_empty_order_with_the_empty_problem_not_the_status_one()
    {
        Spec.For(new ConfirmOrderEvolver())
            .Given(CreatedWith())
            .When(state => ConfirmOrderDecider.Decide(state, Now))
            .ThenFails(OrderProblems.Empty());
    }

    [Fact]
    public void Refuses_an_order_that_is_already_confirmed()
    {
        Spec.For(new ConfirmOrderEvolver())
            .Given(CreatedWith(Widget), new OrderConfirmed(OrderId, Now))
            .When(state => ConfirmOrderDecider.Decide(state, Now))
            .ThenFails(OrderProblems.InvalidStatus("confirmed", OrderStatus.Confirmed));
    }
}
