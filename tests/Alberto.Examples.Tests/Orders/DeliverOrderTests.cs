using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class DeliverOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b005-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b005-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static IEvent[] Confirmed() =>
    [
        new OrderCreated(OrderId, CustomerId, [], null),
        new OrderConfirmed(OrderId, Now)
    ];

    [Fact]
    public void Delivers_a_shipped_order()
    {
        Spec.For(new DeliverOrderEvolver())
            .Given([.. Confirmed(), new OrderShipped(OrderId, "TRACK-1", "DHL", Now)])
            .When(state => DeliverOrderDecider.Decide(state, Now))
            .ThenEmitsOnly<OrderDelivered>(e => e.OrderId == OrderId)
            .ThenState(s => s.Status.Should().Be(OrderStatus.Delivered));
    }

    [Fact]
    public void Reports_Confirmed_for_an_order_that_was_never_shipped()
    {
        Spec.For(new DeliverOrderEvolver())
            .Given(Confirmed())
            .When(state => DeliverOrderDecider.Decide(state, Now))
            .ThenFails("order.invalid-status")
            .ThenProblems(problems => problems.Single().Message.Should().Contain("Confirmed"));
    }
}
