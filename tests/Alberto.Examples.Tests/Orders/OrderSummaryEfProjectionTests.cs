using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using Alberto.Orders.Platform;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

/// <summary>
/// The EF projection folded on its own. Nothing here knows that these documents end up in
/// relational columns rather than the JSONB the overview projection uses — a declaration is a
/// declaration, and the storage it is later attached to is not what these tests are about.
/// </summary>
public sealed class OrderSummaryEfProjectionTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b008-0000-7000-8000-000000000001");
    private static readonly Guid OtherOrderId = Guid.Parse("0197b008-0000-7000-8000-000000000011");
    private static readonly Guid CustomerId = Guid.Parse("0197b008-0000-7000-8000-000000000002");
    private static readonly Guid Widget = Guid.Parse("0197b008-0000-7000-8000-000000000003");
    private static readonly Guid Gadget = Guid.Parse("0197b008-0000-7000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Document(Guid orderId) => orderId.ToString();

    private static OrderCreated Created(Guid orderId) =>
        new(orderId, CustomerId, [new OrderLineItem(Widget, "Widget", 2, 9.99m)], "gift wrap");

    [Fact]
    public void Builds_a_summary_from_the_order_that_was_created()
    {
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .GivenNoEvents()
            .When(Created(OrderId))
            .ThenDocument(Document(OrderId), summary =>
            {
                summary.OrderId.Should().Be(OrderId);
                summary.CustomerId.Should().Be(CustomerId);
                summary.Status.Should().Be(OrderStatus.Draft);
                summary.Notes.Should().Be("gift wrap");
                summary.Total.Should().Be(19.98m);
                summary.LineItems.Single().ProductName.Should().Be("Widget");
            });
    }

    [Fact]
    public void Keys_one_document_per_order()
    {
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .Given(Created(OrderId), Created(OtherOrderId))
            .ThenDocumentCount(2)
            .ThenDocument(Document(OtherOrderId), summary => summary.OrderId.Should().Be(OtherOrderId));
    }

    [Fact]
    public void Adding_a_line_re_totals_the_order()
    {
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .Given(Created(OrderId))
            .When(new OrderItemAdded(OrderId, Gadget, "Gadget", 1, 5.00m))
            .ThenDocument(summary =>
            {
                summary.LineItems.Should().HaveCount(2);
                summary.Total.Should().Be(24.98m);
            });
    }

    [Fact]
    public void Adding_a_product_that_is_already_on_the_order_replaces_its_line_rather_than_doubling_it()
    {
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .Given(Created(OrderId))
            .When(new OrderItemAdded(OrderId, Widget, "Widget", 5, 9.99m))
            .ThenDocument(summary =>
            {
                summary.LineItems.Should().ContainSingle().Which.Quantity.Should().Be(5);
                summary.Total.Should().Be(49.95m);
            });
    }

    [Fact]
    public void Removing_the_last_line_leaves_an_order_worth_nothing_rather_than_no_order()
    {
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .Given(Created(OrderId))
            .When(new OrderItemRemoved(OrderId, Widget))
            .ThenDocument(summary =>
            {
                summary.LineItems.Should().BeEmpty();
                summary.Total.Should().Be(0m);
            });
    }

    [Fact]
    public void Records_where_the_shipment_went_and_when()
    {
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .Given(Created(OrderId), new OrderConfirmed(OrderId, Now))
            .When(new OrderShipped(OrderId, "TRACK-1", "DHL", Now))
            .ThenDocument(summary =>
            {
                summary.Status.Should().Be(OrderStatus.Shipped);
                summary.TrackingNumber.Should().Be("TRACK-1");
                summary.Carrier.Should().Be("DHL");
                summary.ShippedAt.Should().Be(Now);
            });
    }

    [Fact]
    public void Keeps_a_cancelled_order_and_the_reason_it_was_cancelled()
    {
        // A cancelled order is not a deleted one: the read model still has to answer for it.
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .Given(Created(OrderId))
            .When(new OrderCancelled(OrderId, "out of stock", Now))
            .ThenDocument(summary =>
            {
                summary.Status.Should().Be(OrderStatus.Cancelled);
                summary.CancellationReason.Should().Be("out of stock");
                summary.CancelledAt.Should().Be(Now);
            });
    }

    [Fact]
    public void Writes_the_tenant_the_event_arrived_under_onto_the_row()
    {
        // This is the tenant-isolated read model: the tenant is a column the projection fills in
        // from context, which is what lets the query filter on it.
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .ForTenant("acme")
            .When(Created(OrderId))
            .ThenDocument(summary =>
            {
                summary.TenantId.Should().Be("acme");
                summary.DocumentId.Should().Be(Document(OrderId));
            });
    }

    [Fact]
    public void Ignores_an_event_it_has_already_processed()
    {
        // At-least-once delivery means a redelivery is normal, not exceptional. The entity
        // carries the position it last saw, and anything at or below it is a repeat.
        ProjectionSpec.For(OrderSummaryEfProjection.Declaration)
            .Given(Created(OrderId), new OrderConfirmed(OrderId, Now))
            .AtPosition(2)
            .When(new OrderCancelled(OrderId, "out of stock", Now))
            .ThenUnchanged()
            .ThenDocument(summary => summary.Status.Should().Be(OrderStatus.Confirmed));
    }
}
