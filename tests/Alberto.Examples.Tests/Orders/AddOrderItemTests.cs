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

    private static readonly OrderCreated Created = new(OrderId, CustomerId, [], null);

    [Fact]
    public void Adds_an_item_to_a_draft_order()
    {
        Spec.For(new AddOrderItemEvolver())
            .Given(Created)
            .When(state => AddOrderItemDecider.Decide(state, ProductId, "Widget", 2, 9.99m))
            .ThenEmitsOnly<OrderItemAdded>(e => e.ProductId == ProductId && e.Quantity == 2);
    }

    [Fact]
    public void Refuses_an_order_that_does_not_exist()
    {
        Spec.For(new AddOrderItemEvolver())
            .GivenNoEvents()
            .When(state => AddOrderItemDecider.Decide(state, ProductId, "Widget", 2, 9.99m))
            .ThenFails(OrderProblems.NotFound());
    }

    [Fact]
    public void Refuses_a_non_positive_quantity()
    {
        Spec.For(new AddOrderItemEvolver())
            .Given(Created)
            .When(state => AddOrderItemDecider.Decide(state, ProductId, "Widget", 0, 9.99m))
            .ThenFails(OrderProblems.InvalidQuantity());
    }

    [Fact]
    public void Refuses_a_negative_unit_price()
    {
        Spec.For(new AddOrderItemEvolver())
            .Given(Created)
            .When(state => AddOrderItemDecider.Decide(state, ProductId, "Widget", 1, -0.01m))
            .ThenFails(OrderProblems.InvalidUnitPrice());
    }

    // Why this slice folds every status event and not only the one its guard asks about: the
    // refusal names the blocking status, so a slice that stopped folding at Confirmed would
    // report the wrong one for all three statuses beyond it. ThenFails pins the code; the
    // message is what this test is actually about, so it goes through ThenProblems.
    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Shipped")]
    [InlineData("Delivered")]
    [InlineData("Cancelled")]
    public void Names_the_status_that_actually_blocks_it(string status)
    {
        Spec.For(new AddOrderItemEvolver())
            .Given([Created, .. EventsReaching(status)])
            .When(state => AddOrderItemDecider.Decide(state, ProductId, "Widget", 1, 9.99m))
            .ThenFails("order.invalid-status")
            .ThenProblems(problems =>
                problems.Single().Message.Should().Be($"Order cannot be modified in {status} status"));
    }

    private static IEvent[] EventsReaching(string status) => status switch
    {
        "Cancelled" => [new OrderCancelled(OrderId, "changed my mind", At)],
        "Confirmed" => [new OrderConfirmed(OrderId, At)],
        "Shipped" => [new OrderConfirmed(OrderId, At), new OrderShipped(OrderId, "TRACK", "DHL", At)],
        "Delivered" =>
        [
            new OrderConfirmed(OrderId, At),
            new OrderShipped(OrderId, "TRACK", "DHL", At),
            new OrderDelivered(OrderId, At)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status")
    };
}
