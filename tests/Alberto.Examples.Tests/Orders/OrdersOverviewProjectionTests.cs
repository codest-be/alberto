using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

/// <summary>
/// The overview projection folded on its own: events in, the document out. No state store, no
/// database, no control loop — <c>ProjectionSpec</c> runs the declaration and nothing else, which
/// is why this reads like the decider tests next door.
/// </summary>
public sealed class OrdersOverviewProjectionTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b008-0000-7000-8000-000000000001");
    private static readonly Guid OtherOrderId = Guid.Parse("0197b008-0000-7000-8000-000000000011");
    private static readonly Guid CustomerId = Guid.Parse("0197b008-0000-7000-8000-000000000002");
    private static readonly Guid ProductId = Guid.Parse("0197b008-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static OrderCreated Created(Guid orderId, decimal unitPrice = 9.99m, int quantity = 2) =>
        new(orderId, CustomerId, [new OrderLineItem(ProductId, "Widget", quantity, unitPrice)], null);

    [Fact]
    public void Counts_a_new_order_as_a_draft_and_adds_its_line_items_to_revenue()
    {
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .GivenNoEvents()
            .When(Created(OrderId))
            .ThenDocument(OrdersOverviewProjection.DocumentId, overview =>
            {
                overview.TotalOrders.Should().Be(1);
                overview.DraftOrders.Should().Be(1);
                overview.TotalRevenue.Should().Be(19.98m);
                overview.AverageOrderValue.Should().Be(19.98m);
            });
    }

    [Fact]
    public void Confirming_moves_the_order_out_of_draft_without_touching_revenue()
    {
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .Given(Created(OrderId))
            .When(new OrderConfirmed(OrderId, Now))
            .ThenDocument(overview =>
            {
                overview.DraftOrders.Should().Be(0);
                overview.ConfirmedOrders.Should().Be(1);
                overview.TotalOrders.Should().Be(1);
                overview.TotalRevenue.Should().Be(19.98m);
            });
    }

    [Fact]
    public void Walks_an_order_through_the_lifecycle_one_counter_at_a_time()
    {
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .Given(Created(OrderId), new OrderConfirmed(OrderId, Now))
            .When(
                new OrderShipped(OrderId, "TRACK-1", "DHL", Now),
                new OrderDelivered(OrderId, Now))
            .ThenDocument(overview =>
            {
                overview.DraftOrders.Should().Be(0);
                overview.ConfirmedOrders.Should().Be(0);
                overview.ShippedOrders.Should().Be(0);
                overview.DeliveredOrders.Should().Be(1);
            });
    }

    [Fact]
    public void Cancelling_a_draft_moves_it_to_cancelled_and_leaves_the_revenue_it_contributed()
    {
        // Revenue is booked at creation and never unbooked. Whether that is right is a product
        // question; that it is what the projection does is this test's business.
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .Given(Created(OrderId))
            .When(new OrderCancelled(OrderId, "changed their mind", Now))
            .ThenDocument(overview =>
            {
                overview.DraftOrders.Should().Be(0);
                overview.CancelledOrders.Should().Be(1);
                overview.TotalRevenue.Should().Be(19.98m);
            });
    }

    [Fact]
    public void Averages_across_every_order_it_has_seen()
    {
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .Given(Created(OrderId, unitPrice: 10m, quantity: 1))
            .When(Created(OtherOrderId, unitPrice: 30m, quantity: 1))
            .ThenDocument(overview =>
            {
                overview.TotalOrders.Should().Be(2);
                overview.TotalRevenue.Should().Be(40m);
                overview.AverageOrderValue.Should().Be(20m);
            });
    }

    [Fact]
    public void Never_lets_a_counter_go_negative_when_it_meets_a_transition_it_did_not_see_the_start_of()
    {
        // A rebuild that starts mid-log, or a projection added after the fact, sees the second
        // half of a lifecycle with no first half. Math.Max is what keeps that from reading as
        // minus one confirmed order forever.
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .GivenNoEvents()
            .When(new OrderShipped(OrderId, "TRACK-1", "DHL", Now))
            .ThenDocument(overview =>
            {
                overview.ConfirmedOrders.Should().Be(0);
                overview.ShippedOrders.Should().Be(1);
            });
    }

    [Fact]
    public void Ignores_the_events_it_declares_no_handler_for()
    {
        // One log feeds every projection, so most of what reaches this one is not its business.
        // Line-item churn does not move an overview counter and must not create a document.
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .GivenNoEvents()
            .When(new OrderItemAdded(OrderId, ProductId, "Widget", 1, 5m))
            .ThenUnchanged()
            .ThenNoDocuments();
    }

    [Fact]
    public void Stamps_the_document_with_the_time_the_event_was_written()
    {
        var later = Now.AddDays(1);

        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .At(Now).Given(Created(OrderId))
            .At(later).When(new OrderConfirmed(OrderId, Now))
            .ThenDocument(overview =>
            {
                overview.LastOrderAt.Should().Be(Now);
                overview.UpdatedAt.Should().Be(later);
            });
    }

    [Fact]
    public void Folds_every_order_into_the_one_document_the_query_reads()
    {
        // The aggregate's whole point: two orders, still one row.
        ProjectionSpec.For(OrdersOverviewProjection.Declaration)
            .Given(Created(OrderId), Created(OtherOrderId))
            .ThenDocumentCount(1)
            .ThenDocument(OrdersOverviewProjection.DocumentId, overview =>
                overview.TotalOrders.Should().Be(2));
    }
}
