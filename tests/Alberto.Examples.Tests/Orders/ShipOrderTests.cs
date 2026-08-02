using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class ShipOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b004-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b004-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static IEvent[] Confirmed() =>
    [
        new OrderCreated(OrderId, CustomerId, [], null),
        new OrderConfirmed(OrderId, Now)
    ];

    [Fact]
    public void Ships_a_confirmed_order()
    {
        Spec.For(new ShipOrderEvolver())
            .Given(Confirmed())
            .When(state => ShipOrderDecider.Decide(state, "TRACK-1", "DHL", Now))
            .ThenEmitsOnly<OrderShipped>(e => e.TrackingNumber == "TRACK-1")
            .ThenState(s => s.Status.Should().Be(OrderStatus.Shipped));
    }

    [Fact]
    public void Requires_a_tracking_number()
    {
        Spec.For(new ShipOrderEvolver())
            .Given(Confirmed())
            .When(state => ShipOrderDecider.Decide(state, "  ", "DHL", Now))
            .ThenFails(OrderProblems.TrackingNumberRequired());
    }

    [Fact]
    public void Reports_Delivered_when_the_order_has_already_been_delivered()
    {
        Spec.For(new ShipOrderEvolver())
            .Given(
            [
                .. Confirmed(),
                new OrderShipped(OrderId, "TRACK-1", "DHL", Now),
                new OrderDelivered(OrderId, Now)
            ])
            .When(state => ShipOrderDecider.Decide(state, "TRACK-2", "DHL", Now))
            .ThenFails("order.invalid-status")
            .ThenProblems(problems => problems.Single().Message.Should().Contain("Delivered"));
    }

    [Fact]
    public void Handles_only_the_status_events()
    {
        new ShipOrderEvolver().HandledEventTypes.Should().BeEquivalentTo(
            ["order-created", "order-confirmed", "order-shipped", "order-delivered", "order-cancelled"]);
    }
}
