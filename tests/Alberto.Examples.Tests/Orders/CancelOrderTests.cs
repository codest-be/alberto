using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class CancelOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b006-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b006-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static readonly OrderCreated Created = new(OrderId, CustomerId, [], null);

    [Fact]
    public void Cancels_a_draft_order()
    {
        Spec.For(new CancelOrderEvolver())
            .Given(Created)
            .When(state => CancelOrderDecider.Decide(state, "changed my mind", Now))
            .ThenEmitsOnly<OrderCancelled>(e => e.Reason == "changed my mind")
            .ThenState(s => s.Status.Should().Be(OrderStatus.Cancelled));
    }

    [Fact]
    public void Requires_a_reason()
    {
        Spec.For(new CancelOrderEvolver())
            .Given(Created)
            .When(state => CancelOrderDecider.Decide(state, "   ", Now))
            .ThenFails(OrderProblems.CancellationReasonRequired());
    }

    [Fact]
    public void Refuses_to_cancel_a_shipped_order()
    {
        Spec.For(new CancelOrderEvolver())
            .Given(
                Created,
                new OrderConfirmed(OrderId, Now),
                new OrderShipped(OrderId, "TRACK-1", "DHL", Now))
            .When(state => CancelOrderDecider.Decide(state, "too late", Now))
            .ThenFails(OrderProblems.InvalidStatus("cancelled", OrderStatus.Shipped));
    }
}
