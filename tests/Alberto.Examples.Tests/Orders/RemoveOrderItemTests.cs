using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class RemoveOrderItemTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b002-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0197b002-0000-7000-8000-000000000002");

    private static RemoveOrderItemState WithOneItem()
    {
        var evolver = new RemoveOrderItemEvolver();
        var state = evolver.Apply(
            new RemoveOrderItemState(),
            new OrderCreated(OrderId, Guid.NewGuid(), [], null));

        return evolver.Apply(
            state, new OrderItemAdded(OrderId, ProductId, "Widget", 1, 9.99m));
    }

    [Fact]
    public void Removes_an_item_that_is_on_the_order()
    {
        var decision = RemoveOrderItemDecider.Decide(WithOneItem(), ProductId);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderItemRemoved>();
    }

    [Fact]
    public void Refuses_a_product_that_is_not_on_the_order()
    {
        var decision = RemoveOrderItemDecider.Decide(WithOneItem(), Guid.NewGuid());

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.product-not-found");
    }

    [Fact]
    public void Forgets_an_item_that_was_already_removed()
    {
        var state = new RemoveOrderItemEvolver()
            .Apply(WithOneItem(), new OrderItemRemoved(OrderId, ProductId));

        RemoveOrderItemDecider.Decide(state, ProductId).IsError.Should().BeTrue();
    }
}
