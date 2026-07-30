using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class GetOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b007-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0197b007-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Folds_the_whole_order_for_display()
    {
        var evolver = new GetOrderEvolver();
        var state = evolver.Apply(
            new GetOrderState(),
            new OrderCreated(OrderId, Guid.NewGuid(), [], "gift wrap"));
        state = evolver.Apply(state, new OrderItemAdded(OrderId, ProductId, "Widget", 2, 9.99m));
        state = evolver.Apply(state, new OrderConfirmed(OrderId, Now));
        state = evolver.Apply(state, new OrderShipped(OrderId, "TRACK-1", "DHL", Now));

        state.Exists.Should().BeTrue();
        state.Notes.Should().Be("gift wrap");
        state.Status.Should().Be(OrderStatus.Shipped);
        state.TrackingNumber.Should().Be("TRACK-1");
        state.Total.Should().Be(19.98m);
    }

    [Fact]
    public void Reports_an_order_it_has_never_seen_as_absent()
    {
        new GetOrderState().Exists.Should().BeFalse();
    }
}
