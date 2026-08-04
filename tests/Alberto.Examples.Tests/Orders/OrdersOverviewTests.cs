using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class OrdersOverviewTests
{
    [Fact]
    public void Declaration_handles_every_status_changing_event()
    {
        OrdersOverviewProjection.Declaration.HandledEventTypes.Should().BeEquivalentTo(
            ["order-created", "order-confirmed", "order-shipped", "order-delivered", "order-cancelled"]);
    }

    [Fact]
    public void Declaration_keeps_the_processor_id_its_checkpoint_and_state_rows_are_keyed_by()
    {
        OrdersOverviewProjection.Declaration.ProcessorId
            .Should().Be(nameof(OrdersOverviewProjection));
    }
}
