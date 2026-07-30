using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class ShipOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b004-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ShipOrderState Confirmed()
    {
        var evolver = new ShipOrderEvolver();
        var state = evolver.Apply(
            new ShipOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));

        return evolver.Apply(state, new OrderConfirmed(OrderId, Now));
    }

    [Fact]
    public void Ships_a_confirmed_order()
    {
        var decision = ShipOrderDecider.Decide(Confirmed(), "TRACK-1", "DHL", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderShipped>()
            .Which.TrackingNumber.Should().Be("TRACK-1");
    }

    [Fact]
    public void Requires_a_tracking_number()
    {
        var decision = ShipOrderDecider.Decide(Confirmed(), "  ", "DHL", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.tracking-number-required");
    }

    [Fact]
    public void Reports_Delivered_when_the_order_has_already_been_delivered()
    {
        var evolver = new ShipOrderEvolver();
        var state = evolver.Apply(Confirmed(), new OrderShipped(OrderId, "TRACK-1", "DHL", Now));
        state = evolver.Apply(state, new OrderDelivered(OrderId, Now));

        var decision = ShipOrderDecider.Decide(state, "TRACK-2", "DHL", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Message.Should().Contain("Delivered");
    }

    [Fact]
    public void Handles_only_the_status_events()
    {
        new ShipOrderEvolver().HandledEventTypes.Should().BeEquivalentTo(
            ["order-created", "order-confirmed", "order-shipped", "order-delivered", "order-cancelled"]);
    }
}
