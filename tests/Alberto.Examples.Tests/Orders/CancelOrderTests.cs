using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class CancelOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b006-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static CancelOrderState Draft() =>
        new CancelOrderEvolver().Apply(
            new CancelOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));

    [Fact]
    public void Cancels_a_draft_order()
    {
        var decision = CancelOrderDecider.Decide(Draft(), "changed my mind", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderCancelled>()
            .Which.Reason.Should().Be("changed my mind");
    }

    [Fact]
    public void Requires_a_reason()
    {
        var decision = CancelOrderDecider.Decide(Draft(), "   ", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.cancellation-reason-required");
    }

    [Fact]
    public void Refuses_to_cancel_a_shipped_order()
    {
        var evolver = new CancelOrderEvolver();
        var state = evolver.Apply(Draft(), new OrderConfirmed(OrderId, Now));
        state = evolver.Apply(state, new OrderShipped(OrderId, "TRACK-1", "DHL", Now));

        var decision = CancelOrderDecider.Decide(state, "too late", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.invalid-status");
    }
}
