using Alberto.Orders.Features;
using Alberto.Orders.Platform;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class OrderSummariesTests
{
    [Fact]
    public void Connection_reports_more_pages_when_the_window_is_short_of_the_total()
    {
        var connection = new OrdersConnection([], TotalCount: 50, Skip: 0, Take: 20);

        connection.HasNextPage.Should().BeTrue();
        connection.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public void Connection_reports_no_more_pages_on_the_last_window()
    {
        var connection = new OrdersConnection([], TotalCount: 50, Skip: 40, Take: 20);

        connection.HasNextPage.Should().BeFalse();
        connection.HasPreviousPage.Should().BeTrue();
    }

    [Fact]
    public void Projects_a_summary_entity_into_the_graphql_type()
    {
        var entity = new OrderSummaryEntity
        {
            OrderId = Guid.Parse("0197b008-0000-7000-8000-000000000001"),
            CustomerId = Guid.Parse("0197b008-0000-7000-8000-000000000002"),
            Total = 19.98m,
            LineItems =
            [
                new OrderLineItemData
                {
                    ProductId = Guid.Parse("0197b008-0000-7000-8000-000000000003"),
                    ProductName = "Widget",
                    Quantity = 2,
                    UnitPrice = 9.99m,
                    Total = 19.98m
                }
            ]
        };

        var order = Order.FromEntity(entity);

        order.OrderId.Should().Be(entity.OrderId);
        order.LineItems.Single().ProductName.Should().Be("Widget");
    }
}
