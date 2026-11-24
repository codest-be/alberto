using Alberto.Configuration;
using Xunit;

namespace Alberto.Tests.Configuration;

internal sealed class OrderSummaryHandler;

[ProcessorId("orders.legacy-summary")]
internal sealed class RenamedHandler;

internal sealed class GenericHandler<T>;

internal sealed class Outer
{
    internal sealed class Inner;
}

[ProcessorId("  ")]
internal sealed class BlankIdHandler;

internal sealed class Orders
{
    internal sealed class SummaryHandler;
}

internal sealed class Invoices
{
    internal sealed class SummaryHandler;
}

public class ProcessorIdTests
{
    [Fact]
    public void An_unattributed_type_derives_its_own_name()
    {
        Assert.Equal("OrderSummaryHandler", ProcessorId.For<OrderSummaryHandler>());
    }

    [Fact]
    public void The_attribute_wins_over_the_derived_name()
    {
        Assert.Equal("orders.legacy-summary", ProcessorId.For<RenamedHandler>());
    }

    [Fact]
    public void A_nested_type_is_qualified_by_its_declaring_type()
    {
        Assert.Equal("Outer.Inner", ProcessorId.For<Outer.Inner>());
    }

    [Fact]
    public void A_generic_type_includes_its_argument()
    {
        Assert.Equal("GenericHandler_OrderSummaryHandler", ProcessorId.For<GenericHandler<OrderSummaryHandler>>());
    }

    [Fact]
    public void Derivation_is_stable_across_calls()
    {
        Assert.Equal(ProcessorId.For<OrderSummaryHandler>(), ProcessorId.For<OrderSummaryHandler>());
    }

    [Fact]
    public void A_blank_attribute_id_throws_at_the_point_of_declaration()
    {
        var act = () => ProcessorId.For<BlankIdHandler>();

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Matches("(?is)BlankIdHandler.*ProcessorId", exception.Message);
    }

    [Fact]
    public void A_null_type_is_rejected()
    {
        var act = () => ProcessorId.For(null!);

        Assert.ThrowsAny<ArgumentNullException>(act);
    }

    [Fact]
    public void Same_named_handlers_in_different_declaring_types_get_different_ids()
    {
        var ordersId = ProcessorId.For<Orders.SummaryHandler>();
        var invoicesId = ProcessorId.For<Invoices.SummaryHandler>();

        Assert.Equal("Orders.SummaryHandler", ordersId);
        Assert.Equal("Invoices.SummaryHandler", invoicesId);
        Assert.NotEqual(invoicesId, ordersId);
    }
}
