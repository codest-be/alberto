using Alberto.Dcb.Configuration;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class ProcessorIdTests
{
    private sealed class OrderSummaryHandler;

    [ProcessorId("orders.legacy-summary")]
    private sealed class RenamedHandler;

    private sealed class GenericHandler<T>;

    private sealed class Outer
    {
        internal sealed class Inner;
    }

    [ProcessorId("  ")]
    private sealed class BlankIdHandler;

    [Fact]
    public void An_unattributed_type_derives_its_own_name()
    {
        ProcessorId.For<OrderSummaryHandler>().Should().Be("OrderSummaryHandler");
    }

    [Fact]
    public void The_attribute_wins_over_the_derived_name()
    {
        ProcessorId.For<RenamedHandler>().Should().Be("orders.legacy-summary");
    }

    [Fact]
    public void A_nested_type_is_qualified_by_its_declaring_type()
    {
        ProcessorId.For<Outer.Inner>().Should().Be("Outer.Inner");
    }

    [Fact]
    public void A_generic_type_includes_its_argument()
    {
        ProcessorId.For<GenericHandler<OrderSummaryHandler>>()
            .Should().Be("GenericHandler_OrderSummaryHandler");
    }

    [Fact]
    public void Derivation_is_stable_across_calls()
    {
        ProcessorId.For<OrderSummaryHandler>().Should().Be(ProcessorId.For<OrderSummaryHandler>());
    }

    [Fact]
    public void A_blank_attribute_id_throws_at_the_point_of_declaration()
    {
        var act = () => ProcessorId.For<BlankIdHandler>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BlankIdHandler*ProcessorId*");
    }

    [Fact]
    public void A_null_type_is_rejected()
    {
        var act = () => ProcessorId.For(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
