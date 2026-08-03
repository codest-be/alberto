using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class RemoveOrderItemTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b002-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b002-0000-7000-8000-000000000003");
    private static readonly Guid ProductId = Guid.Parse("0197b002-0000-7000-8000-000000000002");

    /// <summary>An order with one item on it, as events.</summary>
    private static IEvent[] WithOneItem() =>
    [
        new OrderCreated(OrderId, CustomerId, [], null),
        new OrderItemAdded(OrderId, ProductId, "Widget", 1, 9.99m)
    ];

    [Fact]
    public void Removes_an_item_that_is_on_the_order()
    {
        Spec.For(new RemoveOrderItemEvolver())
            .Given(WithOneItem())
            .When(state => RemoveOrderItemDecider.Decide(state, ProductId))
            .ThenEmitsOnly<OrderItemRemoved>(e => e.ProductId == ProductId)
            .ThenState(s => s.ProductIds.Should().BeEmpty());
    }

    [Fact]
    public void Refuses_a_product_that_is_not_on_the_order()
    {
        var absent = Guid.Parse("0197b002-0000-7000-8000-00000000ffff");

        Spec.For(new RemoveOrderItemEvolver())
            .Given(WithOneItem())
            .When(state => RemoveOrderItemDecider.Decide(state, absent))
            .ThenFails(OrderProblems.ProductNotFound(absent));
    }

    [Fact]
    public void Forgets_an_item_that_was_already_removed()
    {
        Spec.For(new RemoveOrderItemEvolver())
            .Given([.. WithOneItem(), new OrderItemRemoved(OrderId, ProductId)])
            .When(state => RemoveOrderItemDecider.Decide(state, ProductId))
            .ThenFails(OrderProblems.ProductNotFound(ProductId));
    }
}
