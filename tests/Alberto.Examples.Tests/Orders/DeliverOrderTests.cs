using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class DeliverOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b005-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static DeliverOrderState Shipped()
    {
        var evolver = new DeliverOrderEvolver();
        var state = evolver.Apply(
            new DeliverOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));
        state = evolver.Apply(state, new OrderConfirmed(OrderId, Now));

        return evolver.Apply(state, new OrderShipped(OrderId, "TRACK-1", "DHL", Now));
    }

    [Fact]
    public void Delivers_a_shipped_order()
    {
        var decision = DeliverOrderDecider.Decide(Shipped(), Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderDelivered>();
    }

    [Fact]
    public void Reports_Confirmed_for_an_order_that_was_never_shipped()
    {
        var evolver = new DeliverOrderEvolver();
        var state = evolver.Apply(
            new DeliverOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));
        state = evolver.Apply(state, new OrderConfirmed(OrderId, Now));

        var decision = DeliverOrderDecider.Decide(state, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Message.Should().Contain("Confirmed");
    }
}
