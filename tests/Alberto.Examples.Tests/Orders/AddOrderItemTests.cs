using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class AddOrderItemTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b000-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b000-0000-7000-8000-000000000002");
    private static readonly Guid ProductId = Guid.Parse("0197b000-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset At = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Adds_an_item_to_a_draft_order()
    {
        var decision = AddOrderItemDecider.Decide(AtStatus("Draft"), ProductId, "Widget", 2, 9.99m);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderItemAdded>()
            .Which.ProductId.Should().Be(ProductId);
    }

    [Fact]
    public void Refuses_an_order_that_does_not_exist()
    {
        var decision = AddOrderItemDecider.Decide(
            new AddOrderItemState(), ProductId, "Widget", 2, 9.99m);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.not-found");
    }

    [Fact]
    public void Refuses_a_non_positive_quantity()
    {
        var decision = AddOrderItemDecider.Decide(AtStatus("Draft"), ProductId, "Widget", 0, 9.99m);

        decision.Problems.Single().Code.Should().Be("order.invalid-quantity");
    }

    [Fact]
    public void Refuses_a_negative_unit_price()
    {
        var decision = AddOrderItemDecider.Decide(AtStatus("Draft"), ProductId, "Widget", 1, -0.01m);

        decision.Problems.Single().Code.Should().Be("order.invalid-unit-price");
    }

    // Why this slice folds every status event and not only the one its guard asks about: the
    // refusal names the blocking status, so a slice that stopped folding at Confirmed would
    // report the wrong one for all three statuses beyond it.
    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Shipped")]
    [InlineData("Delivered")]
    [InlineData("Cancelled")]
    public void Names_the_status_that_actually_blocks_it(string status)
    {
        var decision = AddOrderItemDecider.Decide(AtStatus(status), ProductId, "Widget", 1, 9.99m);

        var problem = decision.Problems.Single();
        problem.Code.Should().Be("order.invalid-status");
        problem.Message.Should().Be($"Order cannot be modified in {status} status");
    }

    private static AddOrderItemState AtStatus(string status)
    {
        var evolver = new AddOrderItemEvolver();
        var state = evolver.Apply(new AddOrderItemState(), new OrderCreated(OrderId, CustomerId, [], null));

        if (status is "Draft")
            return state;

        if (status is "Cancelled")
            return evolver.Apply(state, new OrderCancelled(OrderId, "changed my mind", At));

        state = evolver.Apply(state, new OrderConfirmed(OrderId, At));
        if (status is "Confirmed")
            return state;

        state = evolver.Apply(state, new OrderShipped(OrderId, "TRACK", "DHL", At));
        if (status is "Shipped")
            return state;

        return evolver.Apply(state, new OrderDelivered(OrderId, At));
    }
}
